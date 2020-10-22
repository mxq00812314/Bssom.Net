﻿//using System.Runtime.CompilerServices;

using Bssom.Serializer.Binary;
using System.Runtime.CompilerServices;

namespace Bssom.Serializer.BssMap
{
    internal struct BssMapHead
    {
        public int DataLength;
        public int MetaLength;
        public int ElementCount;
        public int MaxDepth;

        public static BssMapHead Read(ref BssomReader reader)
        {
            BssMapHead header = new BssMapHead();
            header.DataLength = reader.ReadVariableNumber();
            header.MetaLength = reader.ReadVariableNumber();
            header.ElementCount = reader.ReadVariableNumber();
            header.MaxDepth = reader.ReadVariableNumber();
            return header;
        }
    }
}