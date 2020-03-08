using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;

namespace ThreadCompressor
{
    /// <summary>
    /// Обрабочик блоков данных
    /// </summary>
    unsafe class ThreadHandler
    {
        /// <summary>
        /// Делегат для запроса нового задания.
        /// </summary>
        /// <param name="handler">Поток обработки блоков</param>
        /// <returns>Должен вернуть true в случае выдачи нового задания.</returns>
        public delegate bool IterEnd(ThreadHandler handler);
        /// <summary>
        /// Вызывается после обработки массива данных, регистрации выполнения, и выдачи нового задания.
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
        /// Компрессия/декомпрессия.
        /// </summary>
        private CompressionMode Mode;


        /// <summary>
        /// Обработано
        /// </summary>
        public bool IsProcessed;
        /// <summary>
        /// Количество байт на запись.
        /// </summary>
        public int ActualBytes;
        /// <summary>
        /// Исходный размер
        /// </summary>
        public int OriginalSize;
        /// <summary>
        /// Указатель на фиксированный массив данных.
        /// </summary>
        public byte* Data;

        /// <summary>
        /// Индекс обрабатываемого блока.
        /// </summary>
        public long Index;

        private byte[] TmpArray;

        /// <summary>
        /// Создает обработчик, и запускает поток обработки данных.
        /// </summary>
        /// <param name="Mode">Режим обработки</param>
        /// <param name="BlockSize">Размер блока на компрессию</param>
        public ThreadHandler(CompressionMode Mode)
        {
            IsWork = true;
            this.Mode = Mode;
            Thread = new Thread(Work);
            Thread.Priority = ThreadPriority.Highest;
            Thread.Start();

            TmpArray = new byte[Constants.BufferBlockSize];
        }

        private void Work()
        {
            while (IsWork)
            {
                if (IterEndEvent != null ? IterEndEvent.Invoke(this) : false)
                {
                    if (Mode == CompressionMode.Compress)
                    {
                        //using (MemoryStream Output = new MemoryStream(Data))
                        using (UnmanagedMemoryStream Output = new UnmanagedMemoryStream(Data, Constants.BlockSize, Constants.BufferBlockSize, FileAccess.Write))
                        {
                            //Array.Copy(Data, 0, TmpArray, 0, Constants.BlockSize);
                            Marshal.Copy((IntPtr)Data, TmpArray, 0, Constants.BlockSize);
                            using (GZipStream gzstream = new GZipStream(Output, CompressionMode.Compress))
                            {
                                gzstream.Write(TmpArray, 0, TmpArray.Length);
                            }
                            //IndexOfFileTale(Data, 0, Constants.BufferBlockSize, 15, ref ActualBytes);
                            IndexOfFileTale(Data, 0, Constants.BufferBlockSize, 15, ref ActualBytes);
                        }
                    }
                    else
                    {
                        //Marshal.Copy((IntPtr)Data, TmpArray, 0, Constants.BlockSize);
                        using (UnmanagedMemoryStream Output = new UnmanagedMemoryStream(Data, ActualBytes, Constants.BufferBlockSize, FileAccess.Read))
                        {
                            using (GZipStream gzstream = new GZipStream(Output, CompressionMode.Decompress))
                            {
                                ActualBytes = gzstream.Read(TmpArray, 0, Constants.BlockSize);
                            }
                            Marshal.Copy(TmpArray, 0, (IntPtr)Data, ActualBytes);
                        }
                    }
                    //IsProcessed = true;
                    IsProcessed = true;
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
        /// Ищем конец сжатого блока
        /// </summary>
        /// <param name="InputArray">Проверяемый массив</param>
        /// <param name="Start">Начало просматриваемого отрезка</param>
        /// <param name="End">Конец просмативаемого отрезка</param>
        /// <param name="Deep">Количество рекурсионных вызовов функции</param>
        /// <param name="Index">Индекс конца блока</param>
        //private void IndexOfFileTale(byte* InputArray, int Start, int End, int Deep, ref int* Index)
        //private void IndexOfFileTale(byte[] InputArray, int Start, int End, int Deep, int Index)
        private void IndexOfFileTale(byte* InputArray, int Start, int End, int Deep, ref int Index)
        {
            if (Deep == 0) return;
            int Сenter = (End + Start) / 2;
            //if (InputArray[Сenter] == 0x00 && InputArray[Сenter + 1] == 0x00)
            if (InputArray[Сenter] == 0x00 && InputArray[Сenter + 1] == 0x00)
            {
                //*Index = Сenter;
                Index = Сenter;
                //IndexOfFileTale(InputArray, Start, Сenter, Deep - 1, Index);
                IndexOfFileTale(InputArray, Start, Сenter, Deep - 1, ref Index);
            }
            else IndexOfFileTale(InputArray, Сenter, End, Deep - 1, ref Index);
            //else IndexOfFileTale(InputArray, Сenter, End, Deep - 1, Index);
        }
    }
}
