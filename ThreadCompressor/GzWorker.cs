using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace ThreadCompressor
{
    /// <summary>
    /// Сжимает/расспаковывает входящий файл
    /// 
    /// ---------Карта сжатого файла----------
    /// 
    /// ________________БОШКА_________________
    /// [4 байта(int) количество сжатых блоков]
    /// 
    /// ________________БЛОКИ_________________
    /// [4 байта(int) размер сжатого блока]
    /// [сам блок произвольного размера]
    /// 
    /// </summary>
    class GzWorker
    {
        private int _CurrentFragmentIndex = -1;     //-1 - костыль для GetNextFragmentIndex()
        private int _CurrentFragmentToWriteIndex;
        private int _FragmentIndexToCalculate;
        private int _Size = 0;                      //Перменная под каст byte[]->int
        private byte[] _SizeData = new byte[4];    //Массив под каст byte[]->int

        private int BlockCount;                     //Количество блоков в файле.
        private int ReadBlockCount;                 //Индекс последнего прочитанного блока.
        private int WriteBlockCount;                //Индекс последнего записанного блока.
        private int CurrentBlockCount;              //Текущий обрабатываемый блок.
        private int ProcessedCount;                 //Количество обработаных блоков.

        private ThreadHandler[] ThreadPool;         //Пул потоков.
        private Thread WriteWorker;                 //Поток на запись данных.

        private AutoResetEvent DataLoading;         //Сигнал о поступлении новых данных.
        private AutoResetEvent FragmentRelease;     //Фрагмент освобожден. Вызывается после записи в файл.
        private AutoResetEvent FragmentProcessed;   //Фрагмент обработан. Вызывается после фиксирования результата упаковки/распаковки блока.

        private string InputFileName;               //Имя исходного файла.
        private string OutputFileName;              //Имя выходного файла.

        private byte[] CompressedBlockPart;         //Остаточная часть с буффера, для декомпрессии.

        private DataFragment[] DataFragments;       //Пул блоков на корм потокам.

        private CompressionMode CompressionMode;    //Упаковка/Распаковка

        private static object GetDataLocker = new object(); //Локер для работы с DataFragments

        private int CurrentFragmentIndex            //Указатель на следующую структуру в DataFragments
        {
            get => _CurrentFragmentIndex;
            set
            {
                _CurrentFragmentIndex = value < DataFragments.Length ? value : 0;
            }
        }
        private int FragmentIndexToCalculate        //Указатель на следующую структуру в DataFragments
        {
            get => _FragmentIndexToCalculate;
            set
            {
                _FragmentIndexToCalculate = value < DataFragments.Length ? value : 0;
            }
        }
        private int CurrentFragmentToWriteIndex     //Указатель на следующий блок, записываемый в файл
        {
            get => _CurrentFragmentToWriteIndex;
            set
            {
                _CurrentFragmentToWriteIndex = value < DataFragments.Length ? value : 0;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="InputFileName">Имя исходного файла.</param>
        /// <param name="OutputFileName">Имя выходного файла.</param>
        public GzWorker(string InputFileName, string OutputFileName)
        {
            DataLoading = new AutoResetEvent(false);
            FragmentRelease = new AutoResetEvent(false);
            FragmentProcessed = new AutoResetEvent(false);
            CompressedBlockPart = new byte[Constants.BufferBlockSize];
            DataFragments = new DataFragment[Environment.ProcessorCount * Constants.DataFragmentCoef];
            for (int i = 0; i < DataFragments.Length; i++)
            {
                DataFragments[i].DataBuffer = new byte[Constants.BufferBlockSize];
            }
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
            this.CompressionMode = CompressionMode;
            Thread.CurrentThread.Priority = CompressionMode == CompressionMode.Decompress ? ThreadPriority.Highest : ThreadPriority.AboveNormal;
            Console.WriteLine(CompressionMode.ToString());
            try
            {
                using (FileStream SR = File.OpenRead(InputFileName))
                {
                    using (FileStream SW = File.Create(OutputFileName))
                    {
                        if (CompressionMode == CompressionMode.Decompress)
                        {
                            ParseFileHeader(ref BlockCount, SR);
                            CheckFreeSpaceForDecompress(BlockCount, OutputFileName);
                        }
                        else CreateFileHeader(ref BlockCount, InputFileName, SW);

                        CreateHandlers(CompressionMode, ref ThreadPool);

                        WriteWorker = new Thread(new ParameterizedThreadStart(WriteResultInFile));
                        WriteWorker.Priority = ThreadPriority.Highest;
                        WriteWorker.Start(SW);

                        LoadData(ref ReadBlockCount,CompressionMode, SR, DataFragments);

                        while (WriteBlockCount != BlockCount) FragmentRelease.WaitOne(5); //Ожидаем завершения записи
                    }
                }
                return "";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            finally
            {
                if (ThreadPool != null)
                {
                    for (int i = 0; i < ThreadPool.Length; i++)
                    {
                        ThreadPool[i]?.Stop();
                        ThreadPool[i] = null;
                    }
                }
                DataFragments = null;
            }
        }

        /// <summary>
        /// Создаем пул потоков в зависимости от количества процессоров
        /// </summary>
        /// <param name="BlockSize">Размер блока исходного файла.</param>
        /// <param name="CompressionMode">Упаковка/Распаковка.</param>
        /// <param name="ThreadPool">Пул потоков.</param>
        private void CreateHandlers(CompressionMode CompressionMode, ref ThreadHandler[] ThreadPool)
        {
            ThreadPool = new ThreadHandler[Environment.ProcessorCount];
            for (int i = 0; i < ThreadPool.Length; i++)
            {
                ThreadPool[i] = new ThreadHandler(CompressionMode);
                ThreadPool[i].IterEndEvent += GzWorker_IterEndEvent;
            }
        }

        /// <summary>
        /// На звершении задания в ThreadHandler.
        /// </summary>
        /// <param name="handler">Поток</param>
        /// <returns>Если задание назначено true</returns>
        private bool GzWorker_IterEndEvent(ThreadHandler handler)
        {
            lock (GetDataLocker)
            {
                CollectResult(ref ProcessedCount, handler, DataFragments);
                return SetWork(ref CurrentBlockCount, ReadBlockCount, handler, DataFragments);
            }
        }

        /// <summary>
        /// Разбор головы файла.
        /// </summary>
        /// <param name="BlockCount">Количество блоков в файле.</param>
        /// <param name="StreamReader">Поток чтения сжатого файла.</param>
        private void ParseFileHeader(ref int BlockCount, FileStream StreamReader)
        {
            StreamReader.Read(_SizeData, 0, 4);
            BlockCount = _SizeData[0] | (_SizeData[1] << 8) | (_SizeData[2] << 16) | (_SizeData[3] << 24);
            if (BlockCount < 1) throw new Exception("Файл поврежден.");
        }

        /// <summary>
        /// Создаем голову сжатого файла.
        /// </summary>
        /// <param name="BlockCount">Количество блоков в файле.</param>
        /// <param name="BlockSize">Размер блока исходного файла.</param>
        /// <param name="InputFileName">Имя исходного файла</param>
        /// <param name="StreamWriter">Поток записи сжатого файла.</param>
        private void CreateFileHeader(ref int BlockCount, string InputFileName, FileStream StreamWriter)
        {
            BlockCount = (int)Math.Ceiling(((double)new FileInfo(InputFileName).Length) / Constants.BlockSize);
            StreamWriter.Write(BitConverter.GetBytes(this.BlockCount), 0, 4);
        }

        /// <summary>
        /// Ожидает следующий <see cref="DataFragment"/> и возвращает индекс на него.
        /// </summary>
        /// <returns></returns>
        private int GetNextFragmentIndex()
        {
            CurrentFragmentIndex++;
            while (DataFragments[CurrentFragmentIndex].OriginalSize != 0) FragmentRelease.WaitOne(10);
            return CurrentFragmentIndex;
        }

        /// <summary>
        /// Загрузка сжатого блока данных.
        /// </summary>
        /// <param name="ReadBlockCount">Индекс последнего прочитанного блока.</param>
        /// <param name="CurrentFragmentIndex">Поток на чтение исходного файла.</param>
        /// <param name="StreamReader">Поток на чтение исходного файла.</param>
        /// <param name="DataFragments">Обрабатываемые данные</param>
        private void LoadCompressedBlock(ref int ReadBlockCount, FileStream StreamReader, DataFragment[] DataFragments)
        {
            while (ReadBlockCount < BlockCount)
            {
                int FragmentIndex = GetNextFragmentIndex();

                StreamReader.Read(_SizeData, 0, 4);                 //Получаем размер блока
                _Size = _SizeData[0] | (_SizeData[1] << 8) | (_SizeData[2] << 16) | (_SizeData[3] << 24);

                StreamReader.Read(DataFragments[FragmentIndex].DataBuffer, 0, _Size);
                DataFragments[FragmentIndex].ActualBytes = Constants.BufferBlockSize;
                DataFragments[FragmentIndex].OriginalSize = _Size;

                //while (_Size < Constants.BufferBlockSize)           //Чистка, нужна ли?
                //{
                //    DataFragments[FragmentIndex].DataBuffer[_Size] = 0;
                //    _Size++;
                //}

                ReadBlockCount++;
                DataLoading.Set();
            }
        }
        /// <summary>
        /// Загружает блок данных исходного файла.
        /// </summary>
        /// <param name="ReadBlockCount">Индекс последнего прочитанного блока.</param>
        /// <param name="BlockSize">Размер блока исходного файла.</param>
        /// <param name="StreamReader">Поток на чтение исходного файла.</param>
        /// <param name="DataFragments">Обрабатываемые данные</param>
        private void LoadBlock(ref int ReadBlockCount, FileStream StreamReader, DataFragment[] DataFragments)
        {
            while (ReadBlockCount < BlockCount)
            {
                int FragmentIndex = GetNextFragmentIndex();

                _Size = StreamReader.Read(DataFragments[FragmentIndex].DataBuffer, 0, Constants.BlockSize);
                DataFragments[FragmentIndex].ActualBytes = Constants.BufferBlockSize;
                DataFragments[FragmentIndex].OriginalSize = _Size;

                while (_Size < Constants.BufferBlockSize)               //Чистка
                {
                    DataFragments[FragmentIndex].DataBuffer[_Size] = 0;
                    _Size++;
                }

                ReadBlockCount++;
                DataLoading.Set();
            }
        }

        /// <summary>
        /// Загрузка данных из файла на обработку.
        /// </summary>
        /// <param name="ReadBlockIndex">Индекс последнего прочитанного блока.</param>
        /// <param name="CompressionMode">Упаковка/Распаковка.</param>
        /// <param name="StreamReader">Поток на чтение исходного файла.</param>
        /// <param name="InputData">Массив данных на обработку.</param>
        private void LoadData(ref int ReadBlockIndex, CompressionMode CompressionMode, FileStream StreamReader, DataFragment[] InputData)
        {
            if (CompressionMode == CompressionMode.Decompress)
            {
                LoadCompressedBlock(ref ReadBlockIndex, StreamReader, InputData);
            }
            else LoadBlock(ref ReadBlockIndex, StreamReader, InputData);
        }

        /// <summary>
        /// Собирает результаты работы потоков.
        /// </summary>
        /// <param name="ProcessedCount">Количество обработаных блоков.</param>
        /// <param name="Handler">Поток.</param>
        private void CollectResult(ref int ProcessedCount, ThreadHandler Handler, DataFragment[] DataFragments)
        {
            if (Handler.Index != -1)
            {
                DataFragments[Handler.Index].ActualBytes = Handler.ActualBytes;
                DataFragments[Handler.Index].IsProcessed = Handler.IsProcessed;
                DataFragments[Handler.Index].OriginalSize = Handler.OriginalSize;

                Handler.Index = -1;
                ProcessedCount++;
                FragmentProcessed.Set();
            }
        }

        /// <summary>
        /// Выставляет работу потокам.
        /// </summary>
        /// <param name="CurrentBlockCount">Текущий обрабатываемый блок.</param>
        /// <param name="ReadBlockCount">Индекс последнего прочитанного блока.</param>
        /// <param name="Handler">Поток.</param>
        /// <param name="InputData">Массив данных на обработку.</param>
        private unsafe bool SetWork(ref int CurrentBlockCount, int ReadBlockCount, ThreadHandler Handler, DataFragment[] DataFragments)
        {
            if (CurrentBlockCount < ReadBlockCount)
            {
                fixed (byte* DP = &DataFragments[FragmentIndexToCalculate].DataBuffer[0]) { Handler.Data = DP; }

                Handler.ActualBytes = DataFragments[FragmentIndexToCalculate].ActualBytes;
                Handler.OriginalSize = DataFragments[FragmentIndexToCalculate].OriginalSize;
                Handler.IsProcessed = DataFragments[FragmentIndexToCalculate].IsProcessed;
                Handler.Index = FragmentIndexToCalculate;

                FragmentIndexToCalculate++;
                CurrentBlockCount++;
                return true;
            }
            else DataLoading.WaitOne(5);
            return false;
        }
        /// <summary>
        /// Пишет результат работы в файл.
        /// </summary>
        /// <param name="InputWriter">Поток на чтение в выходной файл.</param>
        private unsafe void WriteResultInFile(object InputWriter)
        {
            while (WriteBlockCount < BlockCount)
            {
                if (DataFragments[CurrentFragmentToWriteIndex].IsProcessed)
                {
                    if (CompressionMode == CompressionMode.Compress) ((FileStream)InputWriter).Write(BitConverter.GetBytes(DataFragments[CurrentFragmentToWriteIndex].ActualBytes), 0, 4);
                    ((FileStream)InputWriter).Write(DataFragments[CurrentFragmentToWriteIndex].DataBuffer, 0, DataFragments[CurrentFragmentToWriteIndex].ActualBytes);
                    DataFragments[CurrentFragmentToWriteIndex].IsProcessed = false;
                    DataFragments[CurrentFragmentToWriteIndex].OriginalSize = 0;
                    //DataFragments[CurrentFragmentToWriteIndex].ActualBytes = 0;

                    CurrentFragmentToWriteIndex++;
                    FragmentRelease.Set();
                    WriteBlockCount++;
                }
                else FragmentProcessed.WaitOne(5);
            }
        }

        /// <summary>
        /// Проверяет свободное наличие свободного места на диске для целевого файла
        /// </summary>
        /// <param name="BlockSize">Размер блока исходного файла.</param>
        /// <param name="BlockCount">Количество блоков в файле.</param>
        /// <param name="OutputFileName">Имя выходного файла.</param>
        private void CheckFreeSpaceForDecompress(int BlockCount, string OutputFileName)
        {
            var Drive = DriveInfo.GetDrives().Where(x => x.Name == Path.GetPathRoot(new FileInfo(OutputFileName).FullName)).FirstOrDefault();
            if (Drive != null && Drive.AvailableFreeSpace < (long)Constants.BlockSize * BlockCount)
                throw new Exception("Недостаточно места на жестком диске для распаковки.");
        }
    }
}
