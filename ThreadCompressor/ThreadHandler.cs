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
        /// Местный вспомогательный массив.
        /// </summary>
        private byte[] TmpArray;

        /// <summary>
        /// Обработано?
        /// </summary>
        public bool IsProcessed;
        /// <summary>
        /// Количество байт на выходе.
        /// </summary>
        public int ActualBytes;
        /// <summary>
        /// Исходный размер.
        /// </summary>
        public int OriginalSize;
        /// <summary>
        /// Указатель на массив данных.
        /// </summary>
        public byte[] Data;
        /// <summary>
        /// Индекс обрабатываемого блока.
        /// </summary>
        public long Index = -1;

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
                        fixed(byte* datP = &Data[0])
                        {
                            using (UnmanagedMemoryStream Output = new UnmanagedMemoryStream(datP, Constants.BufferBlockSize, Constants.BufferBlockSize, FileAccess.Write))
                            {
                                Marshal.Copy((IntPtr)datP, TmpArray, 0, Constants.BufferBlockSize);                         //Преписываем полностью, затирая старый мусор
                                Marshal.Copy(Constants.EmptyData, 0, (IntPtr)datP, Constants.EmptyData.Length);             //Чистим исходный массив
                                
                                using (GZipStream gzstream = new GZipStream(Output, CompressionMode.Compress))
                                { 
                                    gzstream.Write(TmpArray, 0, OriginalSize);
                                }
                            }
                            IndexOfFileTale(datP, Constants.BufferBlockSize, ref ActualBytes);
                        }
                    }
                    else
                    {
                        fixed (byte* datP = &Data[0])
                        {
                            using (UnmanagedMemoryStream Output = new UnmanagedMemoryStream(datP, OriginalSize, Constants.BufferBlockSize, FileAccess.Read))
                            {
                                using (GZipStream gzstream = new GZipStream(Output, CompressionMode.Decompress))
                                {
                                    ActualBytes = gzstream.Read(TmpArray, 0, Constants.BlockSize);
                                }
                            }
                            Marshal.Copy(TmpArray, 0, (IntPtr)datP, ActualBytes);
                        }
                    }
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
        /// <param name="End">Конец просмативаемого отрезка</param>
        /// <param name="Index">Индекс конца блока</param>
        private void IndexOfFileTale(byte* InputArray, int End, ref int Index)
        {
            while (InputArray[End] == 0x00)
            {
                End--;
            }
            Index = End;
        }
    }
}
