using System;
using System.Diagnostics;
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
        private int BlockCount;                     //Количество блоков в файле.
        private int ReadBlockcCount;                 //Индекс последнего прочитанного блока.
        private int WriteBlockCount;                //Индекс последнего записанного блока.
        private int CurrentBlockCount;              //Текущий обрабатываемый блок.
        private int CurrentFragmentIndex = -1;        //Указатель на следующую структуру в DataFragments
        private int FragmentIndexToCalculate;       //Указатель на следующую структуру в DataFragments
        private int CurrentFragmentToWriteIndex;

        private int ProcessedCount;                 //Количество обработаных блоков.

        private ThreadHandler[] ThreadPool;         //Пул потоков.
        private Thread WriteWorker;                 //Поток на запись данных

        private AutoResetEvent DataLoading;         //Отпуск ThreadHandler'ов
        private AutoResetEvent FragmentRelease;     //Фрагмент освобожден
        private AutoResetEvent FragmentProcessed;     //Фрагмент освобожден

        private string InputFileName;               //Имя исходного файла.
        private string OutputFileName;              //Имя выходного файла.


        private int CompressedBlockPartLength;      //Размер остаточной части
        private byte[] CompressedBlockPart;         //Остаточная часть с буффера, для декомпрессии
        private byte[] ReadBuffer;                  //Буфер подгрузки из файла
        private byte[] WriteBuffer;                 //Буффер на запись

        private DataFragment[] DataFragments;       //Обрабатываемые данные

        private CompressionMode CompressionMode;    //Упаковка/Распаковка

        private static object GetDataLocker = new object(); //Локер для работы с DataFragments
        private static object dd = new object(); 

        /// <summary>
        /// 
        /// </summary>
        /// <param name="InputFileName">Имя исходного файла.</param>
        /// <param name="OutputFileName">Имя выходного файла.</param>
        public GzWorker(string InputFileName, string OutputFileName)
        {
            GC.SuppressFinalize(this);
            DataLoading = new AutoResetEvent(false);
            FragmentRelease = new AutoResetEvent(false);
            FragmentProcessed = new AutoResetEvent(false);
            CompressedBlockPart = new byte[Constants.BufferBlockSize];
            ReadBuffer = new byte[Constants.ReadBufferSize];
            WriteBuffer = new byte[Constants.WriteBufferSize];
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
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
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

                        while (WriteBlockCount != BlockCount)
                        {
                            LoadData(ref ReadBlockcCount, ProcessedCount, BlockCount, CompressionMode, SR, ThreadPool, DataFragments, ref CompressedBlockPart, ReadBuffer);
                            Thread.Sleep(5);
                        }
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
                return SetWork(ref CurrentBlockCount, ReadBlockcCount, handler, DataFragments);
            }
        }

        /// <summary>
        /// Разбор головы файла.
        /// </summary>
        /// <param name="BlockCount">Количество блоков в файле.</param>
        /// <param name="BlockSize">Размер блока исходного файла.</param>
        /// <param name="StreamReader">Поток чтения сжатого файла.</param>
        private void ParseFileHeader(ref int BlockCount, FileStream StreamReader)
        {
            byte[] data = new byte[4];
            StreamReader.Read(data, 0, 4);
            BlockCount = BitConverter.ToInt32(data, 0);
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

        private int GetNextFragmentIndex()
        {
            CurrentFragmentIndex = CurrentFragmentIndex + 1 < DataFragments.Length ? CurrentFragmentIndex + 1 : 0;
            while (DataFragments[CurrentFragmentIndex].OriginalSize != 0)
            {
                FragmentRelease.WaitOne(10);
            }
            return CurrentFragmentIndex;
        }

        /// <summary>
        /// Определяем есть ли потребность в информации на упаковку/распаковку
        /// </summary>
        /// <param name="ThreadPool">Пул потоков.</param>
        /// <returns>Возвращает true в случае необходимости в новой информации.</returns>
        private bool NeedMoreBlocks(int ReadBlockIndex, int ProcessedCount, ThreadHandler[] ThreadPool)
        {
            return ReadBlockIndex < (ProcessedCount + ThreadPool.Length * 5);
        }

        /// <summary>
        /// Устанавливает данные, сигнализирует потокам о новых данных.
        /// </summary>
        /// <param name="ReadBlockCount">Индекс последнего прочитанного блока.</param>
        /// <param name="Data">Данные.</param>
        /// <param name="DataFragments">Обрабатываемые данные.</param>
        private unsafe void SetDataToDataFragments(ref int ReadBlockCount, int StartIndex, int OriginalSize, byte[] Data, DataFragment[] DataFragments)
        {
            int FragmentIndex = GetNextFragmentIndex();

            Array.Copy(Data, StartIndex, DataFragments[FragmentIndex].DataBuffer, 0, OriginalSize);



            DataFragments[FragmentIndex].ActualBytes = Constants.BufferBlockSize;
            DataFragments[FragmentIndex].OriginalSize = OriginalSize;
            ReadBlockCount++;
            DataLoading.Set();
        }

        /// <summary>
        /// Загрузка сжатого блока данных.
        /// </summary>
        /// <param name="ReadBlockIndex">Индекс последнего прочитанного блока.</param>
        /// <param name="StreamReader">Поток на чтение исходного файла.</param>
        /// <param name="DataFragments">Обрабатываемые данные</param>
        /// <param name="CompressedBlockPart">Остаточная часть с буффера</param>
        private void LoadCompressedBlock(ref int ReadBlockIndex, FileStream StreamReader, DataFragment[] DataFragments, ref byte[] CompressedBlockPart, byte[] ReadBuffer)
        {
            int ReadedBytes = StreamReader.Read(ReadBuffer, 0, ReadBuffer.Length);  //количество прочитанных байт, на случай если байт меньше чем буффер
            int OriginSize = 0;                                                     //размер блока
            int index = 0;                                                          //указатель на байт с которого продолжаем чтение
            if (CompressedBlockPartLength != 0) //Обработка остатка
            {
                if (CompressedBlockPartLength < 4) //Если остаток меньше чем размер заголовка файла (крайне маловероятно)
                {
                    Array.Copy(ReadBuffer, 0, CompressedBlockPart, CompressedBlockPartLength, 4 - CompressedBlockPartLength);

                    index = 4 - CompressedBlockPartLength;
                    OriginSize = BitConverter.ToInt32(CompressedBlockPart, 0);
                    Array.Copy(ReadBuffer, index, CompressedBlockPart, 0, OriginSize);
                    index += OriginSize;

                    SetDataToDataFragments(ref ReadBlockIndex, 0, OriginSize, CompressedBlockPart, DataFragments);
                }
                else
                {
                    OriginSize = BitConverter.ToInt32(CompressedBlockPart, 0);

                    Array.Copy(ReadBuffer, 0, CompressedBlockPart, CompressedBlockPartLength, OriginSize - (CompressedBlockPartLength - 4));
                    index = OriginSize - (CompressedBlockPartLength - 4);

                    SetDataToDataFragments(ref ReadBlockIndex, 4, OriginSize, CompressedBlockPart, DataFragments);
                }
            }
            while (index < ReadedBytes) 
            {
                if (index + 4 < ReadBuffer.Length) //Если остаток буффера больше размера заголовка блока
                {
                    OriginSize = BitConverter.ToInt32(ReadBuffer, index);
                    if (OriginSize == 0) return;
                    index += 4;

                    if (OriginSize + index < ReadBuffer.Length) //Если остаток буфера больше размера тела блока
                    {
                        SetDataToDataFragments(ref ReadBlockIndex, index, OriginSize, ReadBuffer, DataFragments);
                        index += OriginSize;
                    }
                    else
                    {
                        SaveToCompressedBlockPart(CompressedBlockPart, ReadBuffer.Length - (index - 4));
                        break;
                    }
                }
                else
                {
                    SaveToCompressedBlockPart(CompressedBlockPart, ReadBuffer.Length - (index - 4));
                    break;
                }
            }
            void SaveToCompressedBlockPart(byte[] CompressedBlockPt, int CompressedBlockPtLen)
            {
                Array.Copy(ReadBuffer, index - 4, CompressedBlockPt, 0, CompressedBlockPtLen);
                CompressedBlockPartLength = CompressedBlockPtLen;
            }
        }
        /// <summary>
        /// Загружает блок данных исходного файла.
        /// </summary>
        /// <param name="ReadBlockIndex">Индекс последнего прочитанного блока.</param>
        /// <param name="BlockSize">Размер блока исходного файла.</param>
        /// <param name="StreamReader">Поток на чтение исходного файла.</param>
        /// <param name="DataFragments">Обрабатываемые данные</param>
        private void LoadBlock(ref int ReadBlockIndex, FileStream StreamReader, DataFragment[] DataFragments, byte[] ReadBuffer)
        {
            int ReadedBytes = StreamReader.Read(ReadBuffer, 0, ReadBuffer.Length);
            for (int i = 0; i < ReadedBytes; i += Constants.BlockSize)
            {
                SetDataToDataFragments(ref ReadBlockIndex, i, Constants.BlockSize, ReadBuffer, DataFragments);
            }
        }

        /// <summary>
        /// Загрузка данных из файла на обработку.
        /// </summary>
        /// <param name="ReadBlockIndex">Индекс последнего прочитанного блока.</param>
        /// <param name="BlockSize">Размер блока исходного файла.</param>
        /// <param name="BlockCount">Количество блоков в файле.</param>
        /// <param name="CompressionMode">Упаковка/Распаковка.</param>
        /// <param name="StreamReader">Поток на чтение исходного файла.</param>
        /// <param name="ThreadPool">Пул потоков.</param>
        /// <param name="InputData">Массив данных на обработку.</param>
        /// <param name="CompressedBlockPart">Остаточная часть с буффера</param>
        private void LoadData(ref int ReadBlockIndex, int ProcessedCount, int BlockCount, CompressionMode CompressionMode, FileStream StreamReader, ThreadHandler[] ThreadPool, DataFragment[] InputData, ref byte[] CompressedBlockPart, byte[] ReadBuffer)
        {
            while (NeedMoreBlocks(ReadBlockIndex, ProcessedCount, ThreadPool))
            {
                if (ReadBlockIndex < BlockCount)
                {
                    if (CompressionMode == CompressionMode.Decompress)
                    {
                        LoadCompressedBlock(ref ReadBlockIndex, StreamReader, InputData, ref CompressedBlockPart, ReadBuffer);
                    }
                    else LoadBlock(ref ReadBlockIndex, StreamReader, InputData, ReadBuffer);
                }
                else break;
            }
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

                //DataFragments[Handler.Index].ActualBytes = IntPtr.Zero;
                //DataFragments[Handler.Index].IsProcessed = IntPtr.Zero;
                //DataFragments[Handler.Index].OriginalSize = IntPtr.Zero;
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
                fixed (byte* DP = &DataFragments[FragmentIndexToCalculate].DataBuffer[0]) Handler.Data = DP;
                //fixed (bool* Prcsd = &DataFragments[FragmentIndexToCalculate].IsProcessed) Handler.IsProcessed = Prcsd;
                //fixed (int* OrSz = &DataFragments[FragmentIndexToCalculate].OriginalSize) Handler.OriginalSize = OrSz;
                //fixed (int* AcB = &DataFragments[FragmentIndexToCalculate].ActualBytes) Handler.ActualBytes = AcB;
                //Handler.Data = DataFragments[FragmentIndexToCalculate].DataBuffer;
                Handler.ActualBytes = DataFragments[FragmentIndexToCalculate].ActualBytes;
                Handler.OriginalSize = DataFragments[FragmentIndexToCalculate].OriginalSize;
                Handler.IsProcessed = DataFragments[FragmentIndexToCalculate].IsProcessed;

                Handler.Index = FragmentIndexToCalculate;

                CurrentBlockCount++;
                FragmentIndexToCalculate = FragmentIndexToCalculate + 1 < DataFragments.Length ? FragmentIndexToCalculate + 1 : 0;
                return true;
            }
            else 
            {
                //Stopwatch w = Stopwatch.StartNew();
                DataLoading.WaitOne(5);
                //w.Stop();
                //Console.WriteLine(w.Elapsed);
                //w = null;
            }
            return false;
        }

        /// <summary>
        /// Пишет результат работы в файл.
        /// </summary>
        /// <param name="InputWriter">Поток на чтение в выходной файл.</param>
        private void WriteResultInFile(object InputWriter)
        {
            while (WriteBlockCount != BlockCount)
            {
                if (DataFragments[CurrentFragmentToWriteIndex].IsProcessed)
                {
                    if (CompressionMode == CompressionMode.Compress) ((FileStream)InputWriter).Write(BitConverter.GetBytes(DataFragments[CurrentFragmentToWriteIndex].ActualBytes), 0, 4);
                    ((FileStream)InputWriter).Write(DataFragments[CurrentFragmentToWriteIndex].DataBuffer, 0, DataFragments[CurrentFragmentToWriteIndex].ActualBytes);
                    DataFragments[CurrentFragmentToWriteIndex].IsProcessed = false;
                    DataFragments[CurrentFragmentToWriteIndex].ActualBytes = 0;
                    DataFragments[CurrentFragmentToWriteIndex].OriginalSize = 0;
                    CurrentFragmentToWriteIndex = CurrentFragmentToWriteIndex + 1 < DataFragments.Length ? CurrentFragmentToWriteIndex + 1 : 0;
                    WriteBlockCount++;
                    FragmentRelease.Set();
                }
                else FragmentProcessed.WaitOne(1);
            }


            //while (WriteBlockCount != BlockCount)
            //{
            //    int BlockCount = 0;     //Количество блоков подготавливаемых к записи
            //    int TotalBytes = 0;     //Количество байт на запись


            //    while (BlockCount < Constants.MaxWriteBlocks                                                                        //Ограничиваем количество данных за раз
            //        && WriteBlockCount + BlockCount != this.BlockCount                                                              //Не выходим за рамки массива данными
            //        && DataFragments[(CurrentFragmentToWriteIndex + BlockCount) % DataFragments.Length].IsProcessed)                 //Фрагмент обработан
            //    {
            //        //Собираем информацию о блоках идущих на запись в диск
            //        if (CompressionMode == CompressionMode.Compress) TotalBytes += 4;
            //        TotalBytes += DataFragments[(CurrentFragmentToWriteIndex + BlockCount) % DataFragments.Length].ActualBytes;
            //        BlockCount++;
            //    }

            //    int CurrentByte = 0;    //Указатель на текущий байт

            //    for (int i = 0; i < BlockCount; i++)
            //    {
            //        //Забиваем буффер данными
            //        if (CompressionMode == CompressionMode.Compress)
            //        {
            //            Array.Copy(BitConverter.GetBytes(DataFragments[CurrentFragmentToWriteIndex].ActualBytes), 0, WriteBuffer, CurrentByte, 4);
            //            CurrentByte += 4;
            //        }

            //        Array.Copy(DataFragments[CurrentFragmentToWriteIndex].DataBuffer, 0, WriteBuffer, CurrentByte, DataFragments[CurrentFragmentToWriteIndex].ActualBytes);

            //        DataFragments[CurrentFragmentToWriteIndex].IsProcessed = false;
            //        DataFragments[CurrentFragmentToWriteIndex].ActualBytes = 0;
            //        DataFragments[CurrentFragmentToWriteIndex].OriginalSize = 0;
            //        FragmentRelease.Set();
            //        CurrentFragmentToWriteIndex = CurrentFragmentToWriteIndex + 1 < DataFragments.Length ? CurrentFragmentToWriteIndex + 1 : 0;
            //    }
            //    if (TotalBytes > 0)//Если буфер не пустой 
            //    {
            //        //Пишем в файл
            //        ((FileStream)InputWriter).Write(WriteBuffer, 0, TotalBytes);
            //        ((FileStream)InputWriter).Flush();
            //        WriteBlockCount += BlockCount;
            //    }
            //    Thread.Sleep(500 / ThreadPool.Length);
            //}
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
