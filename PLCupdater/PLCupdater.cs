using System;
using System.Diagnostics;
using System.Threading;
using Raspberry.IO.GeneralPurpose;

namespace PLCupdater
{
    class PLCupdater
    {
        public static Timer timer;
        public static int idleTimeout = 60000;
        static void Main(string[] args)
        {
            startIdleTimer();
            //create instance of settings object to get settings from config file
            Properties.Settings settings = new Properties.Settings();

            //flag to prevent triggering update while one is already in progress
            bool updating = false;

            //pin definitions
            
            //red LED on pin 11, provision as a low-level pin
            ProcessorPin redLED = ConnectorPin.P1Pin11.ToProcessor();

            //green LED on pin 7, provision as a managed output
            OutputPinConfiguration greenLED = ConnectorPin.P1Pin07.Output();

            //create a low-level connection driver for red LED
            IGpioConnectionDriver driver = GpioConnectionSettings.DefaultDriver;
            driver.Allocate(redLED, PinDirection.Output);

            //create instance of DF1 protocol serial connection class
            DF1Comm.DF1Comm df1 = new DF1Comm.DF1Comm();

            //create high-level connection for green LED and buttons
            //allows for blinking LEDs and OnStatusChanged events for buttons
            GpioConnection gpioConnection = new GpioConnection(greenLED);

            //A button on pin 12, reverse input so that it's normally open instead of normally closed
            //Download program A to PLC when pressed
            InputPinConfiguration Abutton = ConnectorPin.P1Pin12.Input()
                .Revert()
                .OnStatusChanged(a =>
                {
                    //if the button is pressed and update is not currently running, start update
                    if (a && !updating)
                    {
                        //set updating flag to true
                        updating = true;

                        //start update to transfer program A to the PLC using serial port from the config
                        DownloadProgram(df1, driver, gpioConnection, redLED, greenLED, settings.FileA, settings.SerialPort);

                        //set updating flag back to false
                        updating = false;
                    }
                });

            //B button on pin 16, reverse input so that it's normally open instead of normally closed
            //Download program B to PLC when pressed
            var Bbutton = ConnectorPin.P1Pin16.Input()
                .Revert()
                .OnStatusChanged(b =>
                {
                    //if the button is pressed and update is not currently running, start update
                    if (b && !updating)
                    {
                        //set updating flag to true
                        updating = true;

                        //start update to transfer program B to the PLC using serial port from the config
                        //DownloadProgram(df1, driver, gpioConnection, redLED, greenLED, settings.FileB, settings.SerialPort);
                        UploadProgram(df1, driver, gpioConnection, redLED, greenLED, settings.FileA, settings.SerialPort);

                        //set updating flag back to false
                        updating = false;
                    }
                });

            //add the button configurations to the high-level connection 
            gpioConnection.Add(Abutton);
            gpioConnection.Add(Bbutton);

            //prevent program from exiting
            Console.ReadKey();
        }

        //Downloads program to PLC using specified file name and serial port
        private static void DownloadProgram(DF1Comm.DF1Comm df1, IGpioConnectionDriver driver, GpioConnection gpioConnection, ProcessorPin redLED, OutputPinConfiguration greenLED, string filename, string serialPort)
        {
            startIdleTimer();
            //turn on red LED while update is in progress
            driver.Write(redLED, true);

            //set serial port on DF1 class to serial port specified, e.g. "/dev/ttyUSB0"
            df1.ComPort = serialPort;

            //Create new PLCFileDetails object
            System.Collections.ObjectModel.Collection<DF1Comm.DF1Comm.PLCFileDetails> PLCFiles = new System.Collections.ObjectModel.Collection<DF1Comm.DF1Comm.PLCFileDetails>();

            //Create new PLCFile with the defaults
            DF1Comm.DF1Comm.PLCFileDetails PLCFile = default(DF1Comm.DF1Comm.PLCFileDetails);


            //try reading the program file using the filename specified
            try
            {
                //create FileReader to read from file
                System.IO.StreamReader FileReader = new System.IO.StreamReader(filename);

                //initialize string to hold contents of each line
                string line = null;

                //byte collection to hold raw data to send to PLC
                System.Collections.ObjectModel.Collection<byte> data = new System.Collections.ObjectModel.Collection<byte>();

                //loop until the end of the file is reached
                //data is read in chunks of 3 lines
                //the first line is the FileType
                //the second line is the FileNumber
                //and the third line is the data
                //these are converted into a PLCFile and added to the PLCFiles collection
                while (!(FileReader.EndOfStream))
                {
                    //get the contents of the first line
                    line = FileReader.ReadLine();

                    //convert hex ascii to byte for FileType
                    PLCFile.FileType = Convert.ToByte(line, 16);

                    //get the contents of the second line
                    line = FileReader.ReadLine();

                    //convert hex ascii to byte for FileNumber
                    PLCFile.FileNumber = Convert.ToByte(line, 16);

                    //get the contents of the third line
                    line = FileReader.ReadLine();

                    //clear the data collection
                    data.Clear();

                    //loop through the entire line two characters at a time
                    for (int i = 0; i <= line.Length / 2 - 1; i++)
                    {
                        //convert each two character ascii hex byte into a byte and add to data collection
                        data.Add(Convert.ToByte(line.Substring(i * 2, 2), 16));
                    }

                    //create byte array the same length as data collection
                    byte[] dataC = new byte[data.Count];

                    //copy data collection to byte array
                    data.CopyTo(dataC, 0);

                    //assign byte array to PLCFile data property
                    PLCFile.data = dataC;

                    //add the PLCFile to the PLCFiles collection
                    PLCFiles.Add(PLCFile);
                }

                //try to download the PLCfiles to the PLC
                try
                {
                    df1.DownloadProgramData(PLCFiles);
                    //set the PLC back to Run mode when download is complete
                    df1.SetRunMode();
                }

                //write the error to the console if an error occurs downloading the program
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    rapidBlink(driver, redLED);
                    startIdleTimer();
                    return;
                }

                //turn off red LED when update is complete
                driver.Write(redLED, false);

                //write a success message to the console if completed without errors
                Console.WriteLine("Successful Download");

                //turn on green LED for 5 seconds when update is complete
                gpioConnection.Blink(greenLED, TimeSpan.FromSeconds(5));

                //reset the idle shutdown timer
                startIdleTimer();
                return;
            }

            //catch errors reading the program file
            catch (Exception ex)
            {
                //write the error to the console if an error occurs reading the program file
                Console.WriteLine(ex.Message);

                //blink red LED to indicate problem with upload
                rapidBlink(driver, redLED);

                //reset the idle shutdown timer
                startIdleTimer();
                return;
            }
        }

        private static void UploadProgram(DF1Comm.DF1Comm df1, IGpioConnectionDriver driver, GpioConnection gpioConnection, ProcessorPin redLED, OutputPinConfiguration greenLED, string filename, string serialPort)
        {
            startIdleTimer();
            //turn on red LED while update is in progress
            driver.Write(redLED, true);

            //set serial port on DF1 class to serial port specified, e.g. "/dev/ttyUSB0"
            df1.ComPort = serialPort;

            //Create new PLCFileDetails object
            System.Collections.ObjectModel.Collection<DF1Comm.DF1Comm.PLCFileDetails> PLCFiles = new System.Collections.ObjectModel.Collection<DF1Comm.DF1Comm.PLCFileDetails>();

            //byte collection to hold raw data to send to PLC
            System.Collections.ObjectModel.Collection<byte> data = new System.Collections.ObjectModel.Collection<byte>();

            //try to upload the PLCfiles from the PLC
            try
            {
                PLCFiles = df1.UploadProgramData();
                try
                {
                    System.IO.StreamWriter fileWriter = new System.IO.StreamWriter(filename);
                    for (int i = 0; i < PLCFiles.Count; i++)
                    {
                        fileWriter.WriteLine(String.Format("{0:x2}", PLCFiles[i].FileType));
                        fileWriter.WriteLine(String.Format("{0:x2}", PLCFiles[i].FileNumber));
                        for (int j = 0; j < PLCFiles[i].data.Length; j++)
                        {
                            fileWriter.Write(String.Format("{0:x2}", PLCFiles[i].data[j]));
                        }
                        fileWriter.WriteLine();
                    }

                    fileWriter.Close();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Could not save PLC file. " + ex.Message);
                    rapidBlink(driver, redLED);
                    startIdleTimer();
                    return;
                }
            }

            //write the error to the console if an error occurs uploading the program
            catch (Exception ex)
            {
                Console.WriteLine("Could not upload PLC file. " + ex.Message);
                rapidBlink(driver, redLED);
                startIdleTimer();
                return;
            }

            //turn off red LED when upload is complete
            driver.Write(redLED, false);

            //write a success message to the console if completed without errors
            Console.WriteLine("Successful Upload");

            //turn on green LED for 5 seconds when update is complete
            gpioConnection.Blink(greenLED, TimeSpan.FromSeconds(5));

            //reset the idle shutdown timer
            startIdleTimer();
            return;
        }

        static void rapidBlink(IGpioConnectionDriver driver, ProcessorPin led)
        {
            //blink the LED for 5 seconds, 4 times per second
            for (int i = 0; i < 20; i++)
            {
                driver.Write(led, true);
                System.Threading.Thread.Sleep(125);
                driver.Write(led, false);
                System.Threading.Thread.Sleep(125);
            }
        }

        //set the idle shutdown timer to the idleTimeout value
        static void startIdleTimer()
        {
            timer = new Timer(new TimerCallback(idleShutdown), null, idleTimeout, Timeout.Infinite);
        }

        //run the idleshutdown.sh script when the idle shutdown timer has elapsed
        static void idleShutdown(object state)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = "./idleshutdown.sh";
            psi.UseShellExecute = false;
            Process p = Process.Start(psi);
        }
    }
}
