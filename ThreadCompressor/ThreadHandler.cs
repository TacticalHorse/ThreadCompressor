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
        private bool work;
        /// <summary>
        /// Поток обработки.
        /// </summary>
        private Thread thread;
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
        /// Созает обработчик, и запускает поток обработки данных.
        /// </summary>
        /// <param name="Mode">Режим обработки</param>
        /// <param name="BlockSize">Размер блока на компрессию</param>
        public ThreadHandler(CompressionMode Mode, int BlockSize = 1024/1024)
        {
            if (BlockSize < 1024) throw new Exception("Размер блока не может быть меньше 1024 байт");
            work = true;
            this.Mode = Mode;
            this.BlockSize = BlockSize;
            thread = new Thread(Work);
            thread.Start();
        }

        private void Work()
        {
            while (work)
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
                            OutputData = Output.GetBuffer();
                        }
                        //Для сжатого участка отрезаем пустые байты
                        CutEmptyPart(ref OutputData);
                        //И добавляем его размер
                        AddCustomHead(ref OutputData);
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
            work = false;
        }

        /// <summary>
        /// Срезаем хвосты блоков
        /// </summary>
        /// <param name="input"></param>
        private void CutEmptyPart(ref byte[] input)
        {
            int index = IndexOfFileTale(input);
            if (index > -1) Array.Resize(ref input, index);
        }

        /// <summary>
        /// Добавляет длинну в байтах перед. [GZBlock.Length][GZBlock]
        /// </summary>
        /// <param name="GZBlock"></param>
        private void AddCustomHead(ref byte[] GZBlock)
        {
            byte[] intBytes = BitConverter.GetBytes(GZBlock.Length);
            Array.Resize(ref intBytes, GZBlock.Length + 4);
            Array.Copy(GZBlock, 0, intBytes, 4, GZBlock.Length);
            GZBlock = intBytes;
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
