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
        private int LoadBufferSize;                 //Размер фрагмента считываемого из входящего файла за раз

        private ThreadHandler[] ThreadPool;         //Пул потоков.
        private Thread WriteWorker;                 //Поток на запись данных

        private AutoResetEvent DataLoading;         //Отпуск ThreadHandler'ов

        private string InputFileName;               //Имя исходного файла.
        private string OutputFileName;              //Имя выходного файла.


        private byte[] CompressedBlockPart;         //Остаточная часть с буффера, для декомпрессии

        private DataFragment[] DataFragments;       //Обрабатываемые данные

        private CompressionMode CompressionMode;    //Упаковка/Распаковка

        private static object GetDataLocker = new object(); //Локер для работы с DataFragments

        /// <summary>
        /// 
        /// </summary>
        /// <param name="InputFileName">Имя исходного файла.</param>
        /// <param name="OutputFileName">Имя выходного файла.</param>
        public GzWorker(string InputFileName, string OutputFileName)
        {
            BlockSize = 1048576 * 3;
            DataLoading = new AutoResetEvent(false);
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
                            CheckFreeSpaceForDecompress(BlockSize, BlockCount, OutputFileName);
                        }
                        else CreateFileHeader(ref BlockCount, BlockSize, InputFileName, SW);
                        DataFragments = new DataFragment[BlockCount];

                        CreateHandlers(BlockSize, CompressionMode, ref ThreadPool);
                        LoadBufferSize = BlockSize * ThreadPool.Length * 5;
                        WriteWorker = new Thread(new ParameterizedThreadStart(WriteResultInFile));
                        WriteWorker.Start(SW);

                        while (WriteBlockIndex != BlockCount)
                        {
                            LoadData(ref ReadBlockIndex, ProcessedCount, BlockSize, BlockCount, LoadBufferSize, CompressionMode, SR, ThreadPool, DataFragments, ref CompressedBlockPart);
                            Thread.Sleep(1);
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
        private void CreateHandlers(int BlockSize, CompressionMode CompressionMode, ref ThreadHandler[] ThreadPool)
        {
            ThreadPool = new ThreadHandler[(int)(Environment.ProcessorCount)];
            for (int i = 0; i < ThreadPool.Length; i++)
            {
                ThreadPool[i] = new ThreadHandler(CompressionMode, BlockSize);
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
                CollectResult(ref ProcessedCount, handler);
                return SetWork(ref CurrentBlockIndex, ReadBlockIndex, handler, DataFragments);
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
            byte[] data = new byte[8];
            StreamReader.Read(data, 0, 8);
            BlockSize = BitConverter.ToInt32(data, 0);
            if (BlockSize < 1) throw new Exception("Файл поврежден.");
            BlockCount = BitConverter.ToInt32(data, 4);
            if (BlockCount < 1) throw new Exception("Файл поврежден.");
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
        /// Определяем есть ли потребность в информации на упаковку/распаковку
        /// </summary>
        /// <param name="ThreadPool">Пул потоков.</param>
        /// <returns>Возвращает true в случае необходимости в новой информации.</returns>
        private bool NeedMoreBlocks(int ReadBlockIndex, int ProcessedCount, ThreadHandler[] ThreadPool)
        {
            //на всякий пожарный ограничим подкачку данных гигом оперативки
            return Process.GetCurrentProcess().WorkingSet64 < (long)1.5 * 1024 * 1024 * 1024
                && ReadBlockIndex < (ProcessedCount + ThreadPool.Length * 5);
        }

        /// <summary>
        /// Устанавливает данные, сигнализирует потокам о новых данных.
        /// </summary>
        /// <param name="ReadBlockIndex">Индекс последнего прочитанного блока.</param>
        /// <param name="Data">Данные.</param>
        /// <param name="DataFragments">Обрабатываемые данные.</param>
        private void SetDataToDataFragments(ref int ReadBlockIndex, byte[] Data, DataFragment[] DataFragments)
        {
            DataFragments[ReadBlockIndex] = new DataFragment() { Data = Data };
            ReadBlockIndex++;
            DataLoading.Set();
        }

        /// <summary>
        /// Загрузка сжатого блока данных.
        /// </summary>
        /// <param name="ReadBlockIndex">Индекс последнего прочитанного блока.</param>
        /// <param name="StreamReader">Поток на чтение исходного файла.</param>
        /// <param name="DataFragments">Обрабатываемые данные</param>
        /// <param name="CompressedBlockPart">Остаточная часть с буффера</param>
        private void LoadCompressedBlock(ref int ReadBlockIndex, int LoadBufferSize, FileStream StreamReader, DataFragment[] DataFragments, ref byte[] CompressedBlockPart)
        {
            byte[] buffer = new byte[LoadBufferSize];
            int readed = StreamReader.Read(buffer, 0, buffer.Length);   //количество прочитанных байт, на случай если байт меньше чем буффер
            int size = 0;                                               //размер блока
            int index = 0;                                              //указатель на байт с которого продолжаем чтение
            if (CompressedBlockPart != null) //Обработка остатка
            {
                if (CompressedBlockPart.Length < 4) //Если остаток меньше чем размер заголовка файла (крайне маловероятно)
                {
                    byte[] sizedata = new byte[4];
                    Array.Copy(CompressedBlockPart, 0, sizedata, 0, CompressedBlockPart.Length);                                //Собираем бошку блока из двух массивов
                    Array.Copy(buffer, 0, sizedata, CompressedBlockPart.Length, CompressedBlockPart.Length - sizedata.Length);

                    index = sizedata.Length;
                    size = BitConverter.ToInt32(sizedata, 0);
                    byte[] block = new byte[size];
                    Array.Copy(buffer, index, block, 0, size);
                    index += size;

                    SetDataToDataFragments(ref ReadBlockIndex, block, DataFragments);
                }
                else
                {
                    size = BitConverter.ToInt32(CompressedBlockPart, 0);

                    byte[] block = new byte[size];
                    Array.Copy(CompressedBlockPart, 4, block, 0, CompressedBlockPart.Length - 4);                               //Собираем тело блока из двух массивов
                    Array.Copy(buffer, 0, block, CompressedBlockPart.Length - 4, size - (CompressedBlockPart.Length - 4));
                    index = size - (CompressedBlockPart.Length - 4);

                    SetDataToDataFragments(ref ReadBlockIndex, block, DataFragments);
                }
            }
            while (true)
            {
                if (index + 4 < buffer.Length) //Если остаток буффера больше размера заголовка блока
                {
                    size = BitConverter.ToInt32(buffer, index);
                    if (size == 0) return;
                    index += 4;

                    if (size + index < buffer.Length) //Если остаток буфера больше размера тела блока
                    {
                        byte[] block = new byte[size];
                        Array.Copy(buffer, index, block, 0, block.Length);
                        index += size;
                        SetDataToDataFragments(ref ReadBlockIndex, block, DataFragments);
                    }
                    else
                    {
                        //Сохраняем остаток в CompressedBlockPart для обработки при следующем заходе
                        CompressedBlockPart = new byte[buffer.Length - (index - 4)];
                        Array.Copy(buffer, index - 4, CompressedBlockPart, 0, CompressedBlockPart.Length);
                        break;
                    }
                }
                else
                {
                    //Сохраняем остаток в CompressedBlockPart для обработки при следующем заходе
                    CompressedBlockPart = new byte[buffer.Length - (index - 4)];
                    Array.Copy(buffer, index - 4, CompressedBlockPart, 0, CompressedBlockPart.Length);
                    break;
                }
            }
        }

        /// <summary>
        /// Загружает блок данных исходного файла.
        /// </summary>
        /// <param name="ReadBlockIndex">Индекс последнего прочитанного блока.</param>
        /// <param name="BlockSize">Размер блока исходного файла.</param>
        /// <param name="StreamReader">Поток на чтение исходного файла.</param>
        /// <param name="DataFragments">Обрабатываемые данные</param>
        private void LoadBlock(ref int ReadBlockIndex, int BlockSize, int LoadBufferSize, FileStream StreamReader, DataFragment[] DataFragments)
        {
            byte[] buffer = new byte[LoadBufferSize];
            int readed = StreamReader.Read(buffer, 0, buffer.Length);
            for (int i = 0; i < readed; i += BlockSize)
            {
                byte[] block = i + BlockSize > readed ? new byte[readed - i] : new byte[BlockSize];
                Array.Copy(buffer, i, block, 0, block.Length);
                SetDataToDataFragments(ref ReadBlockIndex, block, DataFragments);
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
        private void LoadData(ref int ReadBlockIndex, int ProcessedCount, int BlockSize, int BlockCount, int LoadBufferSize, CompressionMode CompressionMode, FileStream StreamReader, ThreadHandler[] ThreadPool, DataFragment[] InputData, ref byte[] CompressedBlockPart)
        {
            while (NeedMoreBlocks(ReadBlockIndex, ProcessedCount, ThreadPool))
            {
                if (ReadBlockIndex < BlockCount)
                {
                    if (CompressionMode == CompressionMode.Decompress)
                    {
                        LoadCompressedBlock(ref ReadBlockIndex, LoadBufferSize, StreamReader, InputData, ref CompressedBlockPart);
                    }
                    else LoadBlock(ref ReadBlockIndex, BlockSize, LoadBufferSize, StreamReader, InputData);
                }
                else break;
            }
        }

        /// <summary>
        /// Собирает результаты работы потоков.
        /// </summary>
        /// <param name="ProcessedCount">Количество обработаных блоков.</param>
        /// <param name="Handler">Поток.</param>
        private void CollectResult(ref int ProcessedCount, ThreadHandler Handler)
        {
            if (Handler.Index > -1 && Handler.DataFragment.IsProcessed)
            {
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
        private bool SetWork(ref int CurrentBlockIndex, int ReadBlockIndex, ThreadHandler Handler, DataFragment[] DataFragments)
        {
            if (CurrentBlockIndex < ReadBlockIndex)
            {
                if (Handler.Index == -1)
                {
                    Handler.Index = CurrentBlockIndex;
                    Handler.DataFragment = DataFragments[CurrentBlockIndex];
                    CurrentBlockIndex++;
                    return true;
                }
            }
            else  DataLoading.WaitOne(5); 
            return false;
        }

        /// <summary>
        /// Пишет результат работы в файл.
        /// </summary>
        /// <param name="InputWriter">Поток на чтение в выходной файл.</param>
        private void WriteResultInFile(object InputWriter)
        {
            FileStream StreamWriter = (FileStream)InputWriter;
            while (WriteBlockIndex != BlockCount)
            {
                int count = 0;                                      //Количество блоков подготавливаемых к записи
                int start = WriteBlockIndex;                        //Начиная с блока
                int bytes = 0;                                      //Количество байт на запись

                while (count < 50                                   //Защита жора оперативы, количество на запись не больше 50
                    && start + count != BlockCount                  //Не выходим за рамки массива данными
                    && DataFragments[start + count] != null         //Фрагмент данных существует
                    && DataFragments[start + count].IsProcessed)    //Блок обсчитан
                {
                    //Собираем информацию о блоках идущих на запись в диск
                    if (CompressionMode == CompressionMode.Compress) bytes += 4; 
                    bytes += DataFragments[start + count].Data.Length;
                    count++;
                }
                byte[] data = new byte[bytes];                      //Буффер на запись
                int crntbyte = 0;                                   //Указатель на текущий байт
                for (int i = 0; i < count; i++)
                {
                    //Забиваем буффер данными
                    if (CompressionMode == CompressionMode.Compress)
                    {
                        Array.Copy(BitConverter.GetBytes(DataFragments[i + start].Data.Length), 0, data, crntbyte, 4);
                        crntbyte += 4;
                    }
                    Array.Copy(DataFragments[start + i].Data, 0, data, crntbyte, DataFragments[start + i].Data.Length);
                    crntbyte += DataFragments[start + i].Data.Length;
                    DataFragments[start + i] = null;
                }
                if (data.Length > 0)//Если буфер не пустой 
                { 
                    //Пишем в файл
                    StreamWriter.Write(data, 0, data.Length); 
                    StreamWriter.Flush();
                    WriteBlockIndex += count;
                }
                Thread.Sleep(250 / ThreadPool.Length);
                GC.Collect();
            }
        }

        /// <summary>
        /// Проверяет свободное наличие свободного места на диске для целевого файла
        /// </summary>
        /// <param name="BlockSize">Размер блока исходного файла.</param>
        /// <param name="BlockCount">Количество блоков в файле.</param>
        /// <param name="OutputFileName">Имя выходного файла.</param>
        private void CheckFreeSpaceForDecompress(int BlockSize, int BlockCount, string OutputFileName)
        {
            var Drive = DriveInfo.GetDrives().Where(x => x.Name == Path.GetPathRoot(new FileInfo(OutputFileName).FullName)).FirstOrDefault();
            if (Drive != null && Drive.AvailableFreeSpace < (long)BlockSize * (long)BlockCount)
                throw new Exception("Недостаточно места на жестком диске для распаковки.");
        }
    }
}
