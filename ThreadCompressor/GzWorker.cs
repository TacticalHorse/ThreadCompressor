using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
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
    /// [16 байт(MD5) хэш исходного файла]
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
        private double _Progress;                   //Текущий прогресс

        private ThreadHandler[] ThreadPool;         //Пул потоков.

        private string InputFileName;               //Имя исходного файла.
        private string OutputFileName;              //Имя выходного файла.

        private byte[] Hash;                        //Хэш исходного файла.
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
                    Console.WriteLine($"{_Progress:N2}%");
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
            this.InputFileName = InputFileName;
            this.OutputFileName = OutputFileName;
        }

        /// <summary>
        /// Начинает упаковку/расспаковку 
        /// </summary>
        /// <param name="CompressionMode">Упаковка/Распаковка</param>
        /// <param name="InputFileName">Входной файл</param>
        /// <param name="OutputFileName">Выходной файл</param>
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
                            ParseFileHeader(ref BlockCount, ref BlockSize, ref Hash, SR);
                        }
                        else
                        {
                            CreateFileHeader(ref BlockCount, ref BlockSize, InputFileName, SW);
                        }

                        CreateHandlers(BlockSize, CompressionMode, ref ThreadPool);

                        InputData = new byte[BlockCount][];
                        OutputData = new byte[BlockCount][];
                        while (true)
                        {
                            LoadData(ref ReadBlockIndex, BlockSize, BlockCount, InputFileName, CompressionMode, SR, ThreadPool, InputData);

                            for (int i = 0; i < ThreadPool.Length; i++)
                            {
                                CollectResult(WriteBlockIndex, ReadBlockIndex, ThreadPool[i], OutputData);
                                SetWork(ref CurrentBlockIndex, ReadBlockIndex, ThreadPool[i], InputData);
                            }

                            WriteResultInFile(ref WriteBlockIndex, CurrentBlockIndex, SW, OutputData);

                            Progress = (double)WriteBlockIndex / (double)BlockCount * 100;
                            Thread.Sleep(1);
                            GC.Collect();
                            if (WriteBlockIndex == BlockCount) break;
                        }

                        for (int i = 0; i < ThreadPool.Length; i++)
                        {
                            ThreadPool[i].Stop();
                            ThreadPool[i] = null;
                        }


                    }
                }
                if (CompressionMode == CompressionMode.Decompress)
                {
                    if (!IsVerifyFile(OutputFileName, Hash))
                    {
                        return "Файл поврежден.";
                    }
                }

                return "";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// Вычисляет MD5 для входящшего файла.
        /// </summary>
        /// <param name="Filename"></param>
        /// <returns></returns>
        private byte[] CalculateMD5(string Filename)
        {
            using (var md5 = MD5.Create())
            {
                using (var stream = File.OpenRead(Filename))
                {
                    return md5.ComputeHash(stream);
                }
            }
        }

        /// <summary>
        /// Сравнивает хэши входящий и файла.
        /// </summary>
        /// <param name="Filename">Файл с которым необходимо сравнить</param>
        /// <param name="OriginalHash">Хэш оригинального файла</param>
        /// <returns></returns>
        private bool IsVerifyFile(string Filename, byte[] OriginalHash)
        {
            Console.WriteLine("Проверяем целостность файла.");
            var NewFileHash = CalculateMD5(Filename);
            for (int i = 0; i < NewFileHash.Length; i++)
            {
                if (NewFileHash[i] != OriginalHash[i]) return false;
            }
            return true;
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
        /// <param name="Hash">Хэш исходного файла.</param>
        /// <param name="StreamReader">Поток чтения сжатого файла.</param>
        private void ParseFileHeader(ref int BlockCount, ref int BlockSize, ref byte[] Hash, FileStream StreamReader)
        {
            byte[] data = new byte[4];
            StreamReader.Read(data, 0, 4);
            BlockSize = BitConverter.ToInt32(data, 0);
            StreamReader.Read(data, 0, 4);
            BlockCount = BitConverter.ToInt32(data, 0);
            Hash = new byte[16];
            StreamReader.Read(Hash, 0, 16);
        }

        /// <summary>
        /// Создаем голову сжатого файла.
        /// </summary>
        /// <param name="BlockCount">Количество блоков в файле.</param>
        /// <param name="BlockSize">Размер блока исходного файла.</param>
        /// <param name="InputFileName">Имя исходного файла</param>
        /// <param name="StreamWriter">Поток записи сжатого файла.</param>
        private void CreateFileHeader(ref int BlockCount, ref int BlockSize, string InputFileName, FileStream StreamWriter)
        {
            BlockSize = 1048576;
            BlockCount = (int)Math.Ceiling(((double)new FileInfo(InputFileName).Length) / BlockSize);
            StreamWriter.Write(BitConverter.GetBytes(this.BlockSize), 0, 4);
            StreamWriter.Write(BitConverter.GetBytes(this.BlockCount), 0, 4);
            StreamWriter.Write(CalculateMD5(InputFileName), 0, 16);
        }
        /// <summary>
        /// Определяем не выходит ли приложение за рамки лимита потребления RAM.
        /// </summary>
        /// <param name="ThreadPool">Пул потоков.</param>
        /// <returns>Возвращает false в случае выхода за рамки.</returns>
        private bool HaveFreeRAM(ThreadHandler[] ThreadPool)
        {
            return Process.GetCurrentProcess().WorkingSet64 < 1024 * 1024 * (ThreadPool.Length / 3 * 100);
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
        /// <param name="blocksize">Размер блока исходного файла.</param>
        /// <param name="BlockCount">Количество блоков в файле.</param>
        /// <param name="InputFileName">Имя входящего файла.</param>
        /// <param name="CompressionMode">Упаковка/Распаковка.</param>
        /// <param name="StreamReader">Поток на чтение исходного файла.</param>
        /// <param name="ThreadPool">Пул потоков.</param>
        /// <param name="InputData">Массив данных на обработку.</param>
        private void LoadData(ref int ReadBlockIndex, int blocksize, int BlockCount, string InputFileName, CompressionMode CompressionMode, FileStream StreamReader, ThreadHandler[] ThreadPool, byte[][] InputData)
        {
            if (HaveFreeRAM(ThreadPool))
            {
                for (int i = 0; i < ThreadPool.Length; i++)
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
                                int lastblocksize = (int)(new FileInfo(InputFileName).Length - ReadBlockIndex * blocksize);
                                LoadBlock(ref ReadBlockIndex, lastblocksize, StreamReader, InputData);
                            }
                            else
                            {
                                LoadBlock(ref ReadBlockIndex, blocksize, StreamReader, InputData);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Собирает результаты работы потоков.
        /// </summary>
        /// <param name="WriteBlockIndex">Индекс последнего записанного блока.</param>
        /// <param name="ReadBlockIndex">Индекс последнего прочитанного блока.</param>
        /// <param name="Handler">Поток.</param>
        /// <param name="OutputData">Массив обработанных данных.</param>
        private void CollectResult(int WriteBlockIndex, int ReadBlockIndex, ThreadHandler Handler, byte[][] OutputData)
        {
            if (WriteBlockIndex < ReadBlockIndex)
            {
                if (Handler.Index > -1 && Handler.InputData == null)
                {
                    OutputData[Handler.Index] = Handler.OutputData;
                    Handler.OutputData = null;
                    Handler.Index = -1;
                }
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
        private void WriteResultInFile(ref int WriteBlockIndex, int CurrentBlockIndex, FileStream StreamWriter, byte[][] OutputData)
        {
            while (WriteBlockIndex < CurrentBlockIndex && OutputData[WriteBlockIndex] != null)
            {
                StreamWriter.Write(OutputData[WriteBlockIndex], 0, OutputData[WriteBlockIndex].Length);
                OutputData[WriteBlockIndex] = null;
                WriteBlockIndex++;
            }
        }
    }
}
