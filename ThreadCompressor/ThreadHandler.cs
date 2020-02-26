using System;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace ThreadCompressor
{
    class ThreadHandler
    {
        /// <summary>
        /// Флаг на работу потока
        /// </summary>
        private bool IsWork;
        /// <summary>
        /// Поток обработки.
        /// </summary>
        private Thread Thread;
        /// <summary>
        /// Исходный размер блока
        /// </summary>
        private int BlockSize = -1;
        /// <summary>
        /// Компрессия/декомпрессия
        /// </summary>
        private CompressionMode Mode;

        /// <summary>
        /// Индекс обрабатываемого блока
        /// </summary>
        public long Index = -1;
        /// <summary>
        /// Данные на обаботку.
        /// </summary>
        public byte[] InputData;
        /// <summary>
        /// Обработанные данные.
        /// </summary>
        public byte[] OutputData;

        /// <summary>
        /// Создает обработчик, и запускает поток обработки данных.
        /// </summary>
        /// <param name="Mode">Режим обработки</param>
        /// <param name="BlockSize">Размер блока на компрессию</param>
        public ThreadHandler(CompressionMode Mode, int BlockSize = 1024*1024)
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
                if (InputData != null) 
                {
                    if (Mode == CompressionMode.Compress)
                    {
                        using (MemoryStream Output = new MemoryStream())
                        {
                            using (GZipStream gzstream = new GZipStream(Output, CompressionMode.Compress))
                            {
                                gzstream.Write(InputData, 0, InputData.Length);
                            }
                            //Для сжатого участка отрезаем пустые байты
                            OutputData = CutEmptyPart(Output.GetBuffer());
                        }
                    }
                    else
                    {
                        OutputData = new byte[BlockSize];
                        int ReadedBytes = 0; //Размер последнего блока будет отличаться от BlockSize
                        using (GZipStream gzstream = new GZipStream(new MemoryStream(InputData), CompressionMode.Decompress))
                        {
                            ReadedBytes = gzstream.Read(OutputData, 0, OutputData.Length);
                        }
                        if (ReadedBytes != BlockSize) Array.Resize(ref OutputData, ReadedBytes); //Потому подтверждаем размер блока
                    }
                    InputData = null;
                }
                else Thread.Sleep(1);
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
        /// <param name="Input"></param>
        /// <returns>Стриженый массив.</returns>
        private byte[] CutEmptyPart(byte[] Input)
        {
            int Index = IndexOfFileTale(Input);
            if (Index > -1) Array.Resize(ref Input, Index);
            return Input;
        }

        /// <summary>
        /// Ищем конец сжатого блока
        /// </summary>
        /// <param name="inputArray"></param>
        /// <returns></returns>
        private int IndexOfFileTale(byte[] inputArray)
        {
            if (inputArray == null || inputArray.Length < 4) return -1;
            int index = inputArray.Length - 1;
            while (inputArray[index] == 0x00 && inputArray[index - 1] == 0x00)
            {
                index = index - 2;
            }
            return index;
        }
    }
}
