using System;

namespace ThreadCompressor
{
    public static class Constants
    {
        public const int BlockSize = 1024 * 1024 *3;
        public const int BufferBlockSize = BlockSize * 2;
        public const int DataFragmentCoef = 15;
        public static int ReadBufferSize => BlockSize * Environment.ProcessorCount * 5;
        public static int WriteBufferSize => BufferBlockSize * MaxWriteBlocks;
        public static int MaxWriteBlocks => Environment.ProcessorCount * 5;
    }
}
