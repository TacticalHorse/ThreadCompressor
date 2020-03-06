namespace ThreadCompressor
{
    /// <summary>
    /// Блок данных
    /// </summary>
    public class DataFragment
    {
        /// <summary>
        /// Данные.
        /// </summary>
        public byte[] Data { get; set; }
        /// <summary>
        /// Если данные обработаны true.
        /// </summary>
        public bool IsProcessed { get; set; }
    }
}
