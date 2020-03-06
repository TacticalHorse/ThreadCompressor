using System;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace ThreadCompressor
{
    /// <summary>
    /// Обрабочик блоков данных
    /// </summary>
    class ThreadHandler
    {
        /// <summary>
        /// Делегат для запроса нового задания.
        /// </summary>
        /// <param name="handler">Поток обработки блоков</param>
        /// <returns>Должен вернуть true в случае выдачи нового задания.</returns>
        public delegate bool IterEnd(ThreadHandler handler);
        /// <summary>
        /// Вызывается после обработки <see cref="DataFragment"/> для сбора результата, и выдачи нового задания.
        /// Если новое задание выдано, возвращает true.
        /// </summary>
        public event IterEnd IterEndEvent;
        /// <summary>
        /// Флаг на работу потока.
        /// </summary>
        private bool IsWork;
        /// <summary>
        /// Поток обработки.
        /// </summary>
        private Thread Thread;
        /// <summary>
        /// Исходный размер блока.
        /// </summary>
        private int BlockSize = -1;
        /// <summary>
        /// Компрессия/декомпрессия.
        /// </summary>
        private CompressionMode Mode;

        /// <summary>
        /// Индекс обрабатываемого блока.
        /// </summary>
        public long Index = -1;

        /// <summary>
        /// Текущий обрабатываемый фрагмент.
        /// </summary>
        public DataFragment DataFragment;

        /// <summary>
        /// Создает обработчик, и запускает поток обработки данных.
        /// </summary>
        /// <param name="Mode">Режим обработки</param>
        /// <param name="BlockSize">Размер блока на компрессию</param>
        public ThreadHandler(CompressionMode Mode, int BlockSize = 1024 * 1024)
        {
            if (BlockSize < 1024) throw new Exception("Размер блока не может быть меньше 1024 байт");
            IsWork = true;
            this.Mode = Mode;
            this.BlockSize = BlockSize;
            Thread = new Thread(Work);
            Thread.Start();
        }

        private void Work()
        {
            while (IsWork)
            {
                if (IterEndEvent != null ? IterEndEvent.Invoke(this) : false)
                {
                    if (Mode == CompressionMode.Compress)
                    {
                        using (MemoryStream Output = new MemoryStream())
                        {
                            using (GZipStream gzstream = new GZipStream(Output, CompressionMode.Compress))
                            {
                                gzstream.Write(DataFragment.Data, 0, DataFragment.Data.Length);
                            }
                            //Для сжатого участка отрезаем пустые байты
                            DataFragment.Data = CutEmptyPart(Output.GetBuffer(),10);
                        }
                    }
                    else
                    {
                        byte[] DecompressedData = new byte[BlockSize];
                        int ReadedBytes = 0; //Размер последнего блока будет отличаться от BlockSize
                        using (GZipStream gzstream = new GZipStream(new MemoryStream(DataFragment.Data), CompressionMode.Decompress))
                        {
                            ReadedBytes = gzstream.Read(DecompressedData, 0, DecompressedData.Length);
                        }
                        if (ReadedBytes != BlockSize) Array.Resize(ref DecompressedData, ReadedBytes); //Потому подтверждаем размер блока
                        DataFragment.Data = DecompressedData;
                    }
                    DataFragment.IsProcessed = true;
                }
            }
        }

        /// <summary>
        /// Останавливаем поток
        /// </summary>
        public void Stop()
        {
            IsWork = false;
        }

        /// <summary>
        /// Срезаем хвосты блоков
        /// </summary>
        /// <param name="Input">Обрабатываемый массив</param>
        /// <param name="Deep">Глубина поиска.</param>
        /// <returns>Стриженый массив.</returns>
        private byte[] CutEmptyPart(byte[] Input, int Deep)
        {
            int Index = -1;
            IndexOfFileTale(Input, 0, Input.Length - 1, Deep, ref Index);
            if (Index > -1) Array.Resize(ref Input, Index);
            return Input;
        }

        /// <summary>
        /// Ищем конец сжатого блока
        /// </summary>
        /// <param name="InputArray">Проверяемый массив</param>
        /// <param name="Start">Начало просматриваемого отрезка</param>
        /// <param name="End">Конец просмативаемого отрезка</param>
        /// <param name="Deep">Количество рекурсионных вызовов функции</param>
        /// <param name="Index">Индекс конца блока</param>
        private void IndexOfFileTale(byte[] InputArray, int Start, int End, int Deep, ref int Index)
        {
            if (Deep == 0) return;
            int Сenter = (End + Start) / 2;
            if (InputArray[Сenter] == 0x00 && InputArray[Сenter + 1] == 0x00)
            {
                Index = Сenter;
                IndexOfFileTale(InputArray, Start, Сenter, Deep - 1, ref Index);
            }
            else IndexOfFileTale(InputArray, Сenter, End, Deep - 1, ref Index);
        }
    }
}
