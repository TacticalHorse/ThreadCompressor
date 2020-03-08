
namespace ThreadCompressor
{
    /// <summary>
    /// Циферки.
    /// </summary>
    public static class Constants
    {
        /// <summary>
        /// Размер читаймого блока, 8мб вышел оптимальным, больше смысла нет, меньше падает время.
        /// </summary>
        public const int BlockSize = 1024 * 1024 *8;
        /// <summary>
        /// Буффер с запасом, тестил на файле сгенереном на Random.NextByte, "сжатый блок" может оказаться больше исходного
        /// </summary>
        public const int BufferBlockSize = 1024 * 1024 * 9;
        /// <summary>
        /// Коеф на пул блоков. Пул блоков (<see cref="GzWorker.DataFragments"/>) определяется количеством процессовров * коеф 
        /// </summary>
        public const int DataFragmentCoef = 10;
    }
}
