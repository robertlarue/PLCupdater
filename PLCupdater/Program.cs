using System;
using Raspberry.IO.GeneralPurpose;

namespace PLCupdater
{
    class Program
    {
        static void Main(string[] args)
        {
            bool updating = false;
            var redLED = ConnectorPin.P1Pin11.ToProcessor();
            var greenLED = ConnectorPin.P1Pin07.Output();
            var driver = GpioConnectionSettings.DefaultDriver;
            driver.Allocate(redLED, PinDirection.Output);
            DF1Comm.DF1Comm df1 = new DF1Comm.DF1Comm();
            var gpioConnection = new GpioConnection(greenLED);
            var Abutton = ConnectorPin.P1Pin12.Input()
                .Revert()
                .OnStatusChanged(a =>
                {
                    if (a && !updating)
                    {
                        updating = true;
                        driver.Write(redLED, true);
                        DownloadProgram(df1, Properties.Settings.Default.FileA, Properties.Settings.Default.SerialPort);
                        driver.Write(redLED, false);
                        gpioConnection.Blink(greenLED, TimeSpan.FromSeconds(5));
                        updating = false;
                    }
                });
            var Bbutton = ConnectorPin.P1Pin16.Input()
                .Revert()
                .OnStatusChanged(b =>
                {
                    if (b && !updating)
                    {
                        updating = true;
                        driver.Write(redLED, true);
                        DownloadProgram(df1, Properties.Settings.Default.FileB, Properties.Settings.Default.SerialPort);
                        driver.Write(redLED, false);
                        gpioConnection.Blink(greenLED, TimeSpan.FromSeconds(5));
                        updating = false;
                    }
                });
            gpioConnection.Add(Abutton);
            gpioConnection.Add(Bbutton);
            Console.Read();
        }

        private static void DownloadProgram(DF1Comm.DF1Comm df1, string filename, string serialPort)
        {

            df1.ComPort = serialPort;
            System.Collections.ObjectModel.Collection<DF1Comm.DF1Comm.PLCFileDetails> PLCFiles = new System.Collections.ObjectModel.Collection<DF1Comm.DF1Comm.PLCFileDetails>();
            DF1Comm.DF1Comm.PLCFileDetails PLCFile = default(DF1Comm.DF1Comm.PLCFileDetails);
            try
            {
                System.IO.StreamReader FileReader = new System.IO.StreamReader(filename);

                string line = null;
                System.Collections.ObjectModel.Collection<byte> data = new System.Collections.ObjectModel.Collection<byte>();

                int linesCount = 0;
                while (!(FileReader.EndOfStream))
                {
                    line = FileReader.ReadLine();
                    PLCFile.FileType = Convert.ToByte(line, 16);
                    line = FileReader.ReadLine();
                    PLCFile.FileNumber = Convert.ToByte(line, 16);

                    line = FileReader.ReadLine();
                    data.Clear();
                    for (int i = 0; i <= line.Length / 2 - 1; i++)
                    {
                        data.Add(Convert.ToByte(line.Substring(i * 2, 2), 16));
                    }
                    byte[] dataC = new byte[data.Count];
                    data.CopyTo(dataC, 0);
                    PLCFile.data = dataC;

                    PLCFiles.Add(PLCFile);
                    linesCount += 1;
                }

                try
                {
                    df1.DownloadProgramData(PLCFiles);
                    df1.SetRunMode();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    return;
                }

                Console.WriteLine("Successful Download");
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return;
            }
        }
    }
}
