using System.Collections.Generic;

namespace DMMusic.Lib.NVorbis.Contracts
{
    interface IHuffman
    {
        int TableBits { get; }
        IReadOnlyList<HuffmanListNode> PrefixTree { get; }
        IReadOnlyList<HuffmanListNode> OverflowList { get; }

        void GenerateTable(IReadOnlyList<int> value, int[] lengthList, int[] codeList);
    }
}
