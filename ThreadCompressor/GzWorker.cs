using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace ThreadCompressor
{
    /// <summary>
    /// Сжимает/расспаковывает входящий файл
    /// 
    /// ---------Карта сжатого файла----------
    /// 
    /// ________________БОШКА_________________
    /// [4 байта(int) размер исходного блока]
    /// [4 байта(int) количество сжатих блоков]
    /// 
    /// ________________БЛОКИ_________________
    /// [4 байта(int) размер сжатого блока]
    /// [сам блок произвольного размера]
    /// 
    /// </summary>
    class GzWorker
    {
        private int BlockCount;                     //Количество блоков в файле.
        private int ReadBlockIndex;                 //Индекс последнего прочитанного блока.
        private int WriteBlockIndex;                //Индекс последнего записанного блока.
        private int CurrentBlockIndex;              //Текущий обрабатываемый блок.
        private int BlockSize;                      //Размер блока исходного файла.
        private int ProcessedCount;                 //Количество обработаных блоков.
        private double _Progress;                   //Текущий прогресс

        private ThreadHandler[] ThreadPool;         //Пул потоков.
        private Thread ThreadPoolMaster;

        private string InputFileName;               //Имя исходного файла.
        private string OutputFileName;              //Имя выходного файла.

        private volatile byte[][] InputData;        //Массив данных на обработку.
        private volatile byte[][] OutputData;       //Массив обработанных данных.

        /// <summary>
        /// Индикация прогресса.
        /// </summary>
        public double Progress
        {
            get => _Progress;
            private set
            {
                if (_Progress != value)
                {
                    _Progress = value;
                    Console.SetCursorPosition(0, 1);
                    Console.WriteLine($"{_Progress:N1}%");
                }
            }
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="InputFileName">Имя исходного файла.</param>
        /// <param name="OutputFileName">Имя выходного файла.</param>
        public GzWorker(string InputFileName, string OutputFileName)
        {
            BlockSize = 1048576*2;
            this.InputFileName = InputFileName;
            this.OutputFileName = OutputFileName;
        }

        /// <summary>
        /// Начинает упаковку/расспаковку 
        /// </summary>
        /// <param name="CompressionMode">Упаковка/Распаковка</param>
        /// <returns>Возвращает ошибку в случае если она произошла, иначе пустую строку.</returns>
        public string Start(CompressionMode CompressionMode)
        {
            Console.WriteLine(CompressionMode.ToString());
            try
            {
                using (FileStream SR = File.OpenRead(InputFileName))
                {
                    using (FileStream SW = File.Create(OutputFileName))
                    {

                        if (CompressionMode == CompressionMode.Decompress)
                        {
                            ParseFileHeader(ref BlockCount, ref BlockSize, SR);
                        }
                        else
                        {
                            CreateFileHeader(ref BlockCount, BlockSize, InputFileName, SW);
                        }

                        CreateHandlers(BlockSize, CompressionMode, ref ThreadPool);

                        InputData = new byte[BlockCount][];
                        OutputData = new byte[BlockCount][];
                        ThreadPoolMaster = new Thread(new ThreadStart(ThreadPoolMasterWork));
                        ThreadPoolMaster.Start();
                        while (WriteBlockIndex != BlockCount)
                        {
                            LoadData(ref ReadBlockIndex, ProcessedCount, BlockSize, BlockCount, InputFileName, CompressionMode, SR, ThreadPool, InputData);

                            //for (int i = 0; i < ThreadPool.Length; i++)
                            //{
                            //    CollectResult(ref ProcessedCount, ThreadPool[i], OutputData);
                            //    SetWork(ref CurrentBlockIndex, ReadBlockIndex, ThreadPool[i], InputData);
                            //}

                            WriteResultInFile(ref WriteBlockIndex, CurrentBlockIndex, SW, OutputData, CompressionMode);


                            Progress = Math.Round((double)WriteBlockIndex / (double)BlockCount * 100,1);
                            Thread.Sleep(1);
                            GC.Collect();
                        }

                        for (int i = 0; i < ThreadPool.Length; i++)
                        {
                            ThreadPool[i].Stop();
                            ThreadPool[i] = null;
                        }
                    }
                }
                return "";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        void ThreadPoolMasterWork()
        {
            while (WriteBlockIndex != BlockCount)
            {
                for (int i = 0; i < ThreadPool.Length; i++)
                {
                    CollectResult(ref ProcessedCount, ThreadPool[i], OutputData);
                    SetWork(ref CurrentBlockIndex, ReadBlockIndex, ThreadPool[i], InputData);
                }
                Thread.Sleep(1);
            }

            for (int i = 0; i < ThreadPool.Length; i++)
            {
                ThreadPool[i].Stop();
                ThreadPool[i] = null;
            }
        }

        /// <summary>
        /// Создаем пул потоков в зависимости от количества процессоров
        /// </summary>
        /// <param name="BlockSize">Размер блока исходного файла.</param>
        /// <param name="CompressionMode">Упаковка/Распаковка.</param>
        /// <param name="ThreadPool">Пул потоков.</param>
        private void CreateHandlers(int BlockSize, CompressionMode CompressionMode, ref ThreadHandler[] ThreadPool)
        {
            if (Environment.ProcessorCount > 4)
                ThreadPool = new ThreadHandler[Environment.ProcessorCount - 1];// Один резервим для основного потока.
            else
                ThreadPool = new ThreadHandler[4];
            for (int i = 0; i < ThreadPool.Length; i++)
            {
                ThreadPool[i] = new ThreadHandler(CompressionMode, BlockSize);
            }
        }

        /// <summary>
        /// Разбор головы файла.
        /// </summary>
        /// <param name="BlockCount">Количество блоков в файле.</param>
        /// <param name="BlockSize">Размер блока исходного файла.</param>
        /// <param name="StreamReader">Поток чтения сжатого файла.</param>
        private void ParseFileHeader(ref int BlockCount, ref int BlockSize, FileStream StreamReader)
        {
            byte[] data = new byte[4];
            StreamReader.Read(data, 0, 4);
            BlockSize = BitConverter.ToInt32(data, 0);
            StreamReader.Read(data, 0, 4);
            BlockCount = BitConverter.ToInt32(data, 0);
        }

        /// <summary>
        /// Создаем голову сжатого файла.
        /// </summary>
        /// <param name="BlockCount">Количество блоков в файле.</param>
        /// <param name="BlockSize">Размер блока исходного файла.</param>
        /// <param name="InputFileName">Имя исходного файла</param>
        /// <param name="StreamWriter">Поток записи сжатого файла.</param>
        private void CreateFileHeader(ref int BlockCount, int BlockSize, string InputFileName, FileStream StreamWriter)
        {
            BlockCount = (int)Math.Ceiling(((double)new FileInfo(InputFileName).Length) / BlockSize);
            StreamWriter.Write(BitConverter.GetBytes(this.BlockSize), 0, 4);
            StreamWriter.Write(BitConverter.GetBytes(this.BlockCount), 0, 4);
        }

        /// <summary>
        /// Определяем есть ли потребность в информации на сжатие/распаковку
        /// </summary>
        /// <param name="ThreadPool">Пул потоков.</param>
        /// <returns>Возвращает true в случае необходимости в новой информации.</returns>
        private bool NeedMoreBlocks(int ReadBlockIndex, int ProcessedCount, ThreadHandler[] ThreadPool)
        {
            return ReadBlockIndex < (ProcessedCount + ThreadPool.Length * 2);
        }

        /// <summary>
        /// Определяет последний ли блок читается. Для обрезки 
        /// </summary>
        /// <param name="ReadBlockIndex">Индекс последнего прочитанного блока.</param>
        /// <param name="BlockCount">Количество блоков в файле.</param>
        /// <returns></returns>
        private bool IsLastBlock(int ReadBlockIndex, int BlockCount) => ReadBlockIndex + 1 == BlockCount;

        /// <summary>
        /// Загрузка сжатого блока данных.
        /// </summary>
        /// <param name="ReadBlockIndex">Индекс последнего прочитанного блока.</param>
        /// <param name="StreamReader">Поток на чтение исходного файла.</param>
        /// <param name="inputdata">Массив данных на обработку</param>
        private void LoadCompressedBlock(ref int ReadBlockIndex, FileStream StreamReader, byte[][] inputdata)
        {
            var sizedata = new byte[4];
            StreamReader.Read(sizedata, 0, 4);
            int size = BitConverter.ToInt32(sizedata, 0);
            var arr = new byte[size];
            StreamReader.Read(arr, 0, size);
            inputdata[ReadBlockIndex] = arr;
            ReadBlockIndex++;
        }

        /// <summary>
        /// Загружает блок данных исходного файла.
        /// </summary>
        /// <param name="ReadBlockIndex">Индекс последнего прочитанного блока.</param>
        /// <param name="BlockSize">Размер блока исходного файла.</param>
        /// <param name="StreamReader">Поток на чтение исходного файла.</param>
        /// <param name="InputData">Массив данных на обработку.</param>
        private void LoadBlock(ref int ReadBlockIndex, int BlockSize, FileStream StreamReader, byte[][] InputData)
        {
            var arr = new byte[BlockSize];
            StreamReader.Read(arr, 0, BlockSize);
            InputData[ReadBlockIndex] = arr;
            ReadBlockIndex++;
        }

        /// <summary>
        /// Загрузка данных из файла на обработку.
        /// </summary>
        /// <param name="ReadBlockIndex">Индекс последнего прочитанного блока.</param>
        /// <param name="BlockSize">Размер блока исходного файла.</param>
        /// <param name="BlockCount">Количество блоков в файле.</param>
        /// <param name="InputFileName">Имя входящего файла.</param>
        /// <param name="CompressionMode">Упаковка/Распаковка.</param>
        /// <param name="StreamReader">Поток на чтение исходного файла.</param>
        /// <param name="ThreadPool">Пул потоков.</param>
        /// <param name="InputData">Массив данных на обработку.</param>
        private void LoadData(ref int ReadBlockIndex, int ProcessedCount, int BlockSize, int BlockCount, string InputFileName, CompressionMode CompressionMode, FileStream StreamReader, ThreadHandler[] ThreadPool, byte[][] InputData)
        {
            if (NeedMoreBlocks(ReadBlockIndex, ProcessedCount, ThreadPool))
            {
                for (int i = 0; i < ThreadPool.Length * 2; i++)
                {
                    if (ReadBlockIndex < BlockCount)
                    {
                        if (CompressionMode == CompressionMode.Decompress)
                        {
                            LoadCompressedBlock(ref ReadBlockIndex, StreamReader, InputData);
                        }
                        else
                        {
                            if (IsLastBlock(ReadBlockIndex, BlockCount))
                            {
                                int lastblocksize = (int)(new FileInfo(InputFileName).Length - ReadBlockIndex * BlockSize);
                                LoadBlock(ref ReadBlockIndex, lastblocksize, StreamReader, InputData);
                            }
                            else
                            {
                                LoadBlock(ref ReadBlockIndex, BlockSize, StreamReader, InputData);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Собирает результаты работы потоков.
        /// </summary>
        /// <param name="ProcessedCount">Количество обработаных блоков.</param>
        /// <param name="Handler">Поток.</param>
        /// <param name="OutputData">Массив обработанных данных.</param>
        private void CollectResult(ref int ProcessedCount, ThreadHandler Handler, byte[][] OutputData)
        {
            if (Handler.Index > -1 && Handler.InputData == null)
            {
                OutputData[Handler.Index] = Handler.OutputData;
                Handler.OutputData = null;
                Handler.Index = -1;
                ProcessedCount++;
            }
        }

        /// <summary>
        /// Выставляет работу потокам.
        /// </summary>
        /// <param name="CurrentBlockIndex">Текущий обрабатываемый блок.</param>
        /// <param name="ReadBlockIndex">Индекс последнего прочитанного блока.</param>
        /// <param name="Handler">Поток.</param>
        /// <param name="InputData">Массив данных на обработку.</param>
        private void SetWork(ref int CurrentBlockIndex, int ReadBlockIndex, ThreadHandler Handler, byte[][] InputData)
        {
            if (CurrentBlockIndex < ReadBlockIndex)
            {
                if (Handler.Index == -1)
                {
                    Handler.Index = CurrentBlockIndex;
                    Handler.InputData = InputData[CurrentBlockIndex];
                    InputData[CurrentBlockIndex] = null;
                    CurrentBlockIndex++;
                }
            }
        }

        /// <summary>
        /// Пишет результат работы в файл.
        /// </summary>
        /// <param name="WriteBlockIndex">Индекс последнего записанного блока.</param>
        /// <param name="CurrentBlockIndex">Текущий обрабатываемый блок.</param>
        /// <param name="StreamWriter">Поток на чтение в выходной файл.</param>
        /// <param name="OutputData">Массив обработанных данных.</param>
        /// <param name="CompressionMode">Упаковка/Распаковка.</param>
        private void WriteResultInFile(ref int WriteBlockIndex, int CurrentBlockIndex, FileStream StreamWriter, byte[][] OutputData, CompressionMode CompressionMode)
        {
            while (WriteBlockIndex < CurrentBlockIndex && OutputData[WriteBlockIndex] != null)
            {
                //Если компрессия, пишем размер сжатого блока, предварительная склейка в ThreadHandler сильно жрала ресурсы, отказались от нее
                if (CompressionMode == CompressionMode.Compress) StreamWriter.Write(BitConverter.GetBytes(OutputData[WriteBlockIndex].Length), 0, 4);
                StreamWriter.Write(OutputData[WriteBlockIndex], 0, OutputData[WriteBlockIndex].Length);
                OutputData[WriteBlockIndex] = null;
                WriteBlockIndex++;
            }
        }
    }
}
