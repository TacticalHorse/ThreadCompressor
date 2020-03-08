namespace ThreadCompressor
{
    /// <summary>
    /// Блок данных
    /// </summary>
    public unsafe struct DataFragment
    {
        /// <summary>
        /// Обработано
        /// </summary>
        public bool IsProcessed;
        /// <summary>
        /// Количество байт на запись.
        /// </summary>
        public int ActualBytes;
        /// <summary>
        /// Размер после вычислений
        /// </summary>
        public int OriginalSize;
        /// <summary>
        /// Данные.
        /// </summary>
        public byte[] DataBuffer;
    }
}
