﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Voron.Debugging;
using Voron.Impl;

namespace Voron.Trees
{
    public unsafe class Page
    {
        private readonly byte* _base;
        private readonly PageHeader* _header;

        public int LastMatch;
        public int LastSearchPosition;
        public bool Dirty;
        private readonly int _pageMaxSpace;

        public Page(byte* b, int pageMaxSpace)
        {
            _base = b;
            _pageMaxSpace = pageMaxSpace;
            _header = (PageHeader*)b;
        }

        public long PageNumber { get { return _header->PageNumber; } set { _header->PageNumber = value; } }

        public PageFlags Flags { get { return _header->Flags; } set { _header->Flags = value; } }

        public ushort Lower { get { return _header->Lower; } set { _header->Lower = value; } }

        public ushort Upper { get { return _header->Upper; } set { _header->Upper = value; } }

        public int OverflowSize { get { return _header->OverflowSize; } set { _header->OverflowSize = value; } }

        public ushort* KeysOffsets
        {
            get { return (ushort*)(_base + Constants.PageHeaderSize); }
        }

        public NodeHeader* Search(Slice key, SliceComparer cmp)
        {
            if (NumberOfEntries == 0)
            {
                LastSearchPosition = 0;
                LastMatch = 1;
                return null;
            }

            if (key.Options == SliceOptions.BeforeAllKeys)
            {
                LastSearchPosition = 0;
                LastMatch = 1;
                return GetNode(0);
            }

            if (key.Options == SliceOptions.AfterAllKeys)
            {
                LastMatch = -1;
                LastSearchPosition = NumberOfEntries - 1;
                return GetNode(LastSearchPosition);
            }

            int low = IsLeaf ? 0 : 1;
            int high = NumberOfEntries - 1;
            int position = 0;

            var pageKey = new Slice(SliceOptions.Key);
            bool matched = false;
            NodeHeader* node = null;
            while (low <= high)
            {
                position = (low + high) >> 1;

                node = GetNode(position);
                pageKey.Set(node);

                LastMatch = key.Compare(pageKey, cmp);
                matched = true;
                if (LastMatch == 0)
                    break;

                if (LastMatch > 0)
                    low = position + 1;
                else
                    high = position - 1;
            }

            if (matched == false)
            {
                node = GetNode(position);
                LastMatch = key.Compare(pageKey, cmp);
            }

            if (LastMatch > 0) // found entry less than key
                position++; // move to the smallest entry larger than the key

            Debug.Assert(position < ushort.MaxValue);
            LastSearchPosition = position;

            if (position >= NumberOfEntries)
                return null;
            return node;
        }

        public NodeHeader* GetNode(int n)
        {
            Debug.Assert(n >= 0 && n < NumberOfEntries);

            var nodeOffset = KeysOffsets[n];
            var nodeHeader = (NodeHeader*)(_base + nodeOffset);

            return nodeHeader;
        }


        public bool IsLeaf
        {
            get { return _header->Flags==(PageFlags.Leaf); }
        }

        public bool IsBranch
        {
            get { return _header->Flags==(PageFlags.Branch); }
        }

		public bool IsOverflow
		{
			get { return _header->Flags==(PageFlags.Overflow); }
		}

        public ushort NumberOfEntries
        {
            get
            {
                // Because we store the keys offset from the end of the head to lower
                // we can calculate the number of entries by getting the size and dividing
                // in 2, since that is the size of the offsets we use

                return (ushort)((_header->Lower - Constants.PageHeaderSize) >> 1);
            }
        }

        public void RemoveNode(int index)
        {
            Debug.Assert(IsBranch == false || index > 0 || NumberOfEntries == 2); // cannot remove implicit left in branches, unless as part of removing this node entirely
            Debug.Assert(index < NumberOfEntries);

            var node = GetNode(index);

            var size = SizeOf.NodeEntry(node);

            var nodeOffset = KeysOffsets[index];

            int modifiedEntries = 0;
            for (int i = 0; i < NumberOfEntries; i++)
            {
                if (i == index)
                    continue;
                KeysOffsets[modifiedEntries] = KeysOffsets[i];
                if (KeysOffsets[i] < nodeOffset)
                    KeysOffsets[modifiedEntries] += (ushort)size;
                modifiedEntries++;
            }

            NativeMethods.memmove(_base + Upper + size, _base + Upper, nodeOffset - Upper);

            Lower -= (ushort)Constants.NodeOffsetSize;
            Upper += (ushort)size;

        }

        public byte* AddNode(int index, Slice key, int len, long pageNumber)
        {
            Debug.Assert(index <= NumberOfEntries && index >= 0);
            Debug.Assert(IsBranch == false || index != 0 || key.Size == 0);// branch page's first item must be the implicit ref
            if (HasSpaceFor(key, len) == false)
                throw new InvalidOperationException("The page is full and cannot add an entry, this is probably a bug");

            // move higher pointers up one slot
            for (int i = NumberOfEntries; i > index; i--)
            {
                KeysOffsets[i] = KeysOffsets[i - 1];
            }
            var nodeSize = SizeOf.NodeEntry(_pageMaxSpace, key, len);
            var node = AllocateNewNode(index, key, nodeSize);

            if (key.Options == SliceOptions.Key)
                key.CopyTo((byte*)node + Constants.NodeHeaderSize);

			if (len < 0) // branch or overflow
            {
                Debug.Assert(pageNumber != -1);
                node->PageNumber = pageNumber;
	            node->Flags = NodeFlags.PageRef;
                return null; // write nothing here
            }

            Debug.Assert(key.Options == SliceOptions.Key);
            var dataPos = (byte*)node + Constants.NodeHeaderSize + key.Size;
            node->DataSize = len;
	        node->Flags = NodeFlags.Data;
            return dataPos;
        }

        /// <summary>
        /// Internal method that is used when splitting pages
        /// No need to do any work here, we are always adding at the end
        /// </summary>
        internal void CopyNodeDataToEndOfPage(NodeHeader* other, Slice key = null)
        {
            Debug.Assert(SizeOf.NodeEntry(other) + Constants.NodeOffsetSize <= SizeLeft);
            
            var index = NumberOfEntries;

            var nodeSize = SizeOf.NodeEntry(other);

            key = key ?? new Slice(other);

            Debug.Assert(IsBranch == false || index != 0 || key.Size == 0);// branch page's first item must be the implicit ref


            var newNode = AllocateNewNode(index, key, nodeSize);
            newNode->Flags = other->Flags;
            key.CopyTo((byte*)newNode + Constants.NodeHeaderSize);

            if (IsBranch || other->Flags==(NodeFlags.PageRef))
            {
                newNode->PageNumber = other->PageNumber;
                newNode->Flags = NodeFlags.PageRef;
                return;
            }
            newNode->DataSize = other->DataSize;
            NativeMethods.memcpy((byte*)newNode + Constants.NodeHeaderSize + other->KeySize,
                                 (byte*)other + Constants.NodeHeaderSize + other->KeySize,
                                 other->DataSize);
        }


        private NodeHeader* AllocateNewNode(int index, Slice key, int nodeSize)
        {
            var newNodeOffset = (ushort)(_header->Upper - nodeSize);
            Debug.Assert(newNodeOffset >= _header->Lower + Constants.NodeOffsetSize);
            KeysOffsets[index] = newNodeOffset;
            _header->Upper = newNodeOffset;
            _header->Lower += (ushort)Constants.NodeOffsetSize;

            var node = (NodeHeader*)(_base + newNodeOffset);
            node->KeySize = key.Size;
            node->Flags = 0;
            return node;
        }


        public int SizeLeft
        {
            get { return _header->Upper - _header->Lower; }
        }

        public int SizeUsed
        {
            get { return _header->Lower + _pageMaxSpace - _header->Upper; }
        }


        public byte* Base
        {
            get { return _base; }
        }

        public int PageMaxSpace
        {
            get { return _pageMaxSpace; }
        }

        public int LastSearchPositionOrLastEntry
        {

            get
            {
                return LastSearchPosition >= NumberOfEntries
                         ? NumberOfEntries - 1 // after the last entry, but we want to update the last entry
                         : LastSearchPosition;
            }
        }

        public void Truncate(Transaction tx, int i)
        {
            if (i >= NumberOfEntries)
                return;

            // when truncating, we copy the values to a tmp page
            // this has the effect of compacting the page data and avoiding
            // internal page fragmentation
            var copy = tx.TempPage;
            copy.Flags = Flags;
            for (int j = 0; j < i; j++)
            {
                copy.CopyNodeDataToEndOfPage(GetNode(j));
            }
            NativeMethods.memcpy(_base + Constants.PageHeaderSize,
                                 copy._base + Constants.PageHeaderSize,
                                 tx.Environment.PageSize - Constants.PageHeaderSize);

            Upper = copy.Upper;
            Lower = copy.Lower;

            if (LastSearchPosition > i)
                LastSearchPosition = i;
        }

        public int NodePositionFor(Slice key, SliceComparer cmp)
        {
            Search(key, cmp);
            return LastSearchPosition;
        }

        public override string ToString()
        {
            return "#" + PageNumber + " (count: " + NumberOfEntries + ") " + Flags;
        }

        public string Dump()
        {
            var sb = new StringBuilder();
            var slice = new Slice(SliceOptions.Key);
            for (var i = 0; i < NumberOfEntries; i++)
            {
                var n = GetNode(i);
                slice.Set(n);
                sb.Append(slice).Append(", ");
            }
            return sb.ToString();
        }

        public bool HasSpaceFor(Slice key, int len)
        {
            var requiredSpace = GetRequiredSpace(key, len);
            return requiredSpace <= SizeLeft;
        }

        public int GetRequiredSpace(Slice key, int len)
        {
            return SizeOf.NodeEntry(_pageMaxSpace, key, len) + Constants.NodeOffsetSize;
        }

        public string this[int i]
        {
            get { return new Slice(GetNode(i)).ToString(); }
        }

        [Conditional("VALIDATE")]
        public void DebugValidate(Transaction tx, SliceComparer comparer, long root)
        {
            if (NumberOfEntries == 0)
                return;

            var prev = new Slice(GetNode(0));
            var pages = new HashSet<long>();
            for (int i = 1; i < NumberOfEntries; i++)
            {
                var node = GetNode(i);
                var current = new Slice(node);

                if (prev.Compare(current, comparer) >= 0)
                {
                    DebugStuff.RenderAndShow(tx, root, 1);
                    throw new InvalidOperationException("The page " + PageNumber + " is not sorted");
                }

                if (node->Flags==(NodeFlags.PageRef))
                {
                    if (pages.Add(node->PageNumber) == false)
                    {
                        DebugStuff.RenderAndShow(tx, root, 1);
                        throw new InvalidOperationException("The page " + PageNumber + " references same page multiple times");
                    }
                }

                prev = current;
            }
        }
    }
}