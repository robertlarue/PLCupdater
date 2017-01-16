
using Microsoft.VisualBasic;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
//**********************************************************************************************
//* DF1 Data Link Layer & Application Layer
//*
//* Archie Jacobs
//* Manufacturing Automation, LLC
//* ajacobs@mfgcontrol.com
//* 22-NOV-06
//*
//* This class implements the two layers of the Allen Bradley DF1 protocol.
//* In terms of the AB documentation, the data link layer acts as the transmitter and receiver.
//* Communication commands in the format described in chapter 7, are passed to
//* the data link layer using the SendData method.
//*
//* Reference : Allen Bradley Publication 1770-6.5.16
//*
//* Distributed under the GNU General Public License (www.gnu.org)
//*
//* This program is free software; you can redistribute it and/or
//* as published by the Free Software Foundation; either version 2
//* of the License, or (at your option) any later version.
//*
//* This program is distributed in the hope that it will be useful,
//* but WITHOUT ANY WARRANTY; without even the implied warranty of
//* MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//* GNU General Public License for more details.

//* You should have received a copy of the GNU General Public License
//* along with this program; if not, write to the Free Software
//* Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
//*
//*
//* 22-MAR-07  Added floating point read/write capability
//* 23-MAR-07  Added string file read/write
//*              Handle reading of up to 256 elements in one call
//* 24-MAR-07  Corrected ReadAny to allow an array of strings to be read
//* 26-MAR-07  Changed error handling to throw exceptions to comply more with .NET standards
//* 29-MAR-07  When reading multiple sub elements of timers or counters, read all the same sub-element
//* 30-MAR-07  Added GetDataMemory, GetSlotCount, and GetIoConfig  - all were reverse engineered
//* 04-APR-07  Added GetMicroDataMemory
//* 07-APR-07  Reworked the Responded variable to try to fix a small issue during a lot of rapid reads
//* 12-APR-07  Fixed a problem with reading Timers & Counters  more than 39 at a time
//* 01-MAY-07  Fixed a problem with writing multiple elements using WriteData
//* 06-JUN-07  Add the assumption of file number 2 for S file (ParseAddress) e.g. S:1=S2:1
//* 30-AUG-07  Fixed a problem where the value 16 gets doubled, it would not check the last byte
//* 13-FEB-08  Added more errors codes in DecodeMessage, Added the EXT STS if STS=&hF0
//* 13-FEB-08  Added GetML1500DataMemory to work with the MicroLogix 1500
//* 14-FEB-08  Added Reading/Writing of Long data with help from Tony Cicchino
//* 14-FEB-08  Corrected problem when writing an array of Singles to an Integer table
//* 18-FEB-08  Corrected an error in SendData that would not allow it to retry
//* 23-FEB-08  Corrected a problem when reading elements with extended addressing
//* 26-FEB-08  Reconfigured ReadRawData & WriteRawData
//* 28-FEB-08  Completed Downloading & Uploading functions
//**********************************************************************************************
using System.ComponentModel.Design;
using System.Text.RegularExpressions;
[assembly: CLSCompliant(true)]
namespace DF1
{
    public class DF1Comm
    {
        //* create a random number as a TNS starting point
        private static Random rnd = new Random();
        private UInt16 TNS = Convert.ToUInt16((rnd.Next() & 0x7f) + 1);
        private int ProcessorType;
        //* This is used to help problems that come from transmissions errors when using a USB converter

        private int SleepDelay;
        private bool[] Responded = new bool[256];
        private System.Collections.ObjectModel.Collection<byte> QueuedCommand = new System.Collections.ObjectModel.Collection<byte>();

        private bool CommandInQueue;

        private bool DisableEvent;
        public event EventHandler DataReceived;
        public event EventHandler UnsolictedMessageRcvd;
        public event EventHandler AutoDetectTry;
        public event EventHandler DownloadProgress;
        public event EventHandler UploadProgress;


        #region "Properties"
        private int m_MyNode;
        public int MyNode
        {
            get { return m_MyNode; }
            set { m_MyNode = value; }
        }

        private int m_TargetNode;
        public int TargetNode
        {
            get { return m_TargetNode; }
            set { m_TargetNode = value; }
        }

        private int m_BaudRate = 19200;
        public int BaudRate
        {
            get { return m_BaudRate; }
            set
            {
                if (value != m_BaudRate)
                    CloseComms();
                m_BaudRate = value;
            }
        }

        private string m_ComPort = "COM1";
        public string ComPort
        {
            get { return m_ComPort; }
            set
            {
                if (value != m_ComPort)
                    CloseComms();
                m_ComPort = value;
            }
        }

        private System.IO.Ports.Parity m_Parity = System.IO.Ports.Parity.None;
        public System.IO.Ports.Parity Parity
        {
            get { return m_Parity; }
            set
            {
                if (value != m_Parity)
                    CloseComms();
                m_Parity = value;
            }
        }

        private string m_Protocol = "DF1";
        public string Protocol
        {
            get { return m_Protocol; }
            set { m_Protocol = value; }
        }

        public enum CheckSumOptions
        {
            Crc = 0,
            Bcc = 1
        }

        private CheckSumOptions m_CheckSum;
        public CheckSumOptions CheckSum
        {
            get { return m_CheckSum; }
            set { m_CheckSum = value; }
        }
        //**************************************************************
        //* Determine whether to wait for a data read or raise an event
        //**************************************************************
        private bool m_AsyncMode;
        public bool AsyncMode
        {
            get { return m_AsyncMode; }
            set { m_AsyncMode = value; }
        }
        #endregion

        #region "Public Methods"
        //***************************************
        //* COMMAND IMPLEMENTATION SECTION
        //***************************************
        public void SetRunMode()
        {
            //* Get the processor type by using a get status command
            int reply = 0;
            int rTNS = 0;
            byte[] data = new byte[1];
            int Func = 0;

            //* ML1000
            if (GetProcessorType() == 0x58)
            {
                data[0] = 2;
                Func = 0x3a;
            }
            else
            {
                Func = 0x80;
                data[0] = 6;
            }

            reply = PrefixAndSend(0xf, Func, data, true, ref rTNS);

            if (reply != 0)
                throw new DF1Exception("Failed to change to Run mode, Check PLC Key switch - " + DecodeMessage(reply));
        }

        public void SetProgramMode()
        {
            //* Get the processor type by using a get status command
            int reply = 0;
            int rTNS = 0;
            byte[] data = new byte[1];
            int Func = 0;

            //* ML1000
            if (GetProcessorType() == 0x58)
            {
                data[0] = 0;
                Func = 0x3a;
            }
            else
            {
                data[0] = 1;
                Func = 0x80;
            }

            reply = PrefixAndSend(0xf, Func, data, true, ref rTNS);

            if (reply != 0)
                throw new DF1Exception("Failed to change to Program mode, Check PLC Key switch - " + DecodeMessage(reply));
        }


        //Public Sub DisableForces(ByVal targetNode As Integer)
        //    Dim rTNS As Integer
        //    Dim data() As Byte = {}
        //    Dim reply As Integer = PrefixAndSend(TargetNode, &HF, &H41, data, True, rTNS)
        //End Sub

        /// <summary>
        /// Retreives the processor code by using the get status command
        /// </summary>
        /// <returns></returns>
        /// <remarks></remarks>
        public int GetProcessorType()
        {
            //* Get the processor type by using a get status command
            int rTNS = 0;
            byte[] Data = new byte[1];
            if (PrefixAndSend(6, 3, Data, true, ref rTNS) == 0)
            {
                //* Returned data psoition 11 is the first character in the ASCII name of the processor
                //* Position 9 is the code for the processor
                //* &H78 = SLC 5/05
                //* &h89 = ML1500 LSP
                //* &H8C = ML1500 LRP
                //* &H1A = Fixed SLC500
                //* &H18 = SLC 5/01
                //* &H25 = SLC 5/02
                //* &H49 = SLC 5/03
                //* &H5B = SLC 5/04
                //* &H95 = CompactLogix L35E
                //* &H58 = ML1000
                //* &H9C = ML1100
                //* &H88 = ML1200
                ProcessorType = DataPackets[rTNS][9];
            }
            return ProcessorType;
        }


        public struct DataFileDetails
        {
            public string FileType;
            public int FileNumber;
            public int NumberOfElements;
        }

        /// <summary>
        /// Retreives the list of data tables and number of elements in each
        /// </summary>
        /// <returns></returns>
        /// <remarks></remarks>
        public DataFileDetails[] GetDataMemoryX()
        {
            //Data[0] As Byte
            int ProcessorType = GetProcessorType();

            //* See GetProcessorType for codes
            switch (ProcessorType)
            {
                //Case &H89
                //    Return GetML1500DataMemory()
                //Case &H58, &H25
                //    Return GetML1000DataMemory()
                default:
                    break;
                    //Return GetSLCDataMemory()
            }

            throw new DF1Exception("Could Not Get processor type");
        }


        //*******************************************************************
        //* This is the start of reverse engineering to retreive data tables
        //*   Read 12 bytes File #0, Type 1, start at Element 21
        //*    Then extract the number of data and program files
        //*******************************************************************
        /// <summary>
        /// Retreives the list of data tables and number of elements in each
        /// </summary>
        /// <returns></returns>
        /// <remarks></remarks>
        public DataFileDetails[] GetDataMemory()
        {
            //**************************************************
            //* Read the File 0 (Program & data file directory
            //**************************************************
            byte[] FileZeroData = ReadFileDirectory();


            int NumberOfDataTables = FileZeroData[52] + FileZeroData[53] * 256;
            int NumberOfProgramFiles = FileZeroData[46] + FileZeroData[47] * 256;
            //Dim DataFiles(NumberOfDataTables - 1) As DataFileDetails
            System.Collections.ObjectModel.Collection<DataFileDetails> DataFiles = new System.Collections.ObjectModel.Collection<DataFileDetails>();
            int FilePosition = 0;
            int BytesPerRow = 0;
            //*****************************************
            //* Process the data from the data table
            //*****************************************
            switch (ProcessorType)
            {
                case 0x25:
                case 0x58:
                    //*ML1000, SLC 5/02
                    FilePosition = 93;
                    BytesPerRow = 8;
                    break;
                case 0x88:
                case 0x89:
                case 0x8c:
                case 0x9c:
                    //* ML1100, ML1200, ML1500
                    FilePosition = 103;
                    BytesPerRow = 10;
                    break;
                default:
                    //* SLC 5/04, 5/05
                    FilePosition = 79;
                    BytesPerRow = 10;
                    break;
            }


            //* Comb through data file 0 looking for data table definitions
            int i = 0;
            int k = 0;
            int BytesPerElement = 0;
            i = 0;

            DataFileDetails DataFile = new DataFileDetails();
            while (k < NumberOfDataTables & FilePosition < FileZeroData.Length)
            {
                switch (FileZeroData[FilePosition])
                {
                    case 0x82:
                    case 0x8b:
                        DataFile.FileType = "O";
                        BytesPerElement = 2;
                        break;
                    case 0x83:
                    case 0x8c:
                        DataFile.FileType = "I";
                        BytesPerElement = 2;
                        break;
                    case 0x84:
                        DataFile.FileType = "S";
                        BytesPerElement = 2;
                        break;
                    case 0x85:
                        DataFile.FileType = "B";
                        BytesPerElement = 2;
                        break;
                    case 0x86:
                        DataFile.FileType = "T";
                        BytesPerElement = 6;
                        break;
                    case 0x87:
                        DataFile.FileType = "C";
                        BytesPerElement = 6;
                        break;
                    case 0x88:
                        DataFile.FileType = "R";
                        BytesPerElement = 6;
                        break;
                    case 0x89:
                        DataFile.FileType = "N";
                        BytesPerElement = 2;
                        break;
                    case 0x8a:
                        DataFile.FileType = "F";
                        BytesPerElement = 4;
                        break;
                    case 0x8d:
                        DataFile.FileType = "ST";
                        BytesPerElement = 84;
                        break;
                    case 0x8e:
                        DataFile.FileType = "A";
                        BytesPerElement = 2;
                        break;
                    case 0x91:
                        DataFile.FileType = "L";
                        //Long Integer
                        BytesPerElement = 4;
                        break;
                    case 0x92:
                        DataFile.FileType = "MG";
                        //Message Command 146
                        BytesPerElement = 50;
                        break;
                    case 0x93:
                        DataFile.FileType = "PD";
                        //PID
                        BytesPerElement = 46;
                        break;
                    case 0x94:
                        DataFile.FileType = "PLS";
                        //Programmable Limit Swith
                        BytesPerElement = 12;

                        break;
                    default:
                        DataFile.FileType = "Undefined";
                        //* 61h=Program File
                        BytesPerElement = 2;
                        break;
                }
                DataFile.NumberOfElements = (FileZeroData[FilePosition + 1] + FileZeroData[FilePosition + 2] * 256) / BytesPerElement;
                DataFile.FileNumber = i;

                //* Only return valid user data files
                if (FileZeroData[FilePosition] > 0x81 & FileZeroData[FilePosition] < 0x9f)
                {
                    DataFiles.Add(DataFile);
                    //DataFile = New DataFileDetails
                    k += 1;
                }

                //* Index file number once in the region of data files
                if (k > 0)
                    i += 1;
                FilePosition += BytesPerRow;
            }

            //* Move to an array with a length of only good data files
            //Dim GoodDataFiles(k - 1) As DataFileDetails
            DataFileDetails[] GoodDataFiles = new DataFileDetails[DataFiles.Count];
            //For l As Integer = 0 To k - 1
            //    GoodDataFiles(l) = DataFiles(l)
            //Next

            DataFiles.CopyTo(GoodDataFiles, 0);

            return GoodDataFiles;
        }



        //*******************************************************************
        //*   Read the data file directory, File 0, Type 2
        //*    Then extract the number of data and program files
        //*******************************************************************
        private DataFileDetails[] GetML1500DataMemory()
        {
            int reply = 0;
            ParsedDataAddress PAddress = default(ParsedDataAddress);

            //* Get the length of File 0, Type 2. This is the program/data file directory
            PAddress.FileNumber = 0;
            PAddress.FileType = 2;
            PAddress.Element = 0x2f;
            byte[] data = ReadRawData(PAddress, 2, ref reply);


            if (reply == 0)
            {
                int FileZeroSize = data[0] + (data[1]) * 256;

                PAddress.Element = 0;
                PAddress.SubElement = 0;
                //* Read all of File 0, Type 2
                byte[] FileZeroData = ReadRawData(PAddress, FileZeroSize, ref reply);

                //* Start Reading the data table configuration
                DataFileDetails[] DataFiles = new DataFileDetails[257];

                int FilePosition = 0;
                int i = 0;


                //* Process the data from the data table
                if (reply == 0)
                {
                    //* Comb through data file 0 looking for data table definitions
                    int k = 0;
                    int BytesPerElement = 0;
                    i = 0;
                    FilePosition = 143;
                    while (FilePosition < FileZeroData.Length)
                    {
                        switch (FileZeroData[FilePosition])
                        {
                            case 0x89:
                                DataFiles[k].FileType = "N";
                                BytesPerElement = 2;
                                break;
                            case 0x85:
                                DataFiles[k].FileType = "B";
                                BytesPerElement = 2;
                                break;
                            case 0x86:
                                DataFiles[k].FileType = "T";
                                BytesPerElement = 6;
                                break;
                            case 0x87:
                                DataFiles[k].FileType = "C";
                                BytesPerElement = 6;
                                break;
                            case 0x84:
                                DataFiles[k].FileType = "S";
                                BytesPerElement = 2;
                                break;
                            case 0x8a:
                                DataFiles[k].FileType = "F";
                                BytesPerElement = 4;
                                break;
                            case 0x8d:
                                DataFiles[k].FileType = "ST";
                                BytesPerElement = 84;
                                break;
                            case 0x8e:
                                DataFiles[k].FileType = "A";
                                BytesPerElement = 2;
                                break;
                            case 0x88:
                                DataFiles[k].FileType = "R";
                                BytesPerElement = 6;
                                break;
                            case 0x82:
                            case 0x8b:
                                DataFiles[k].FileType = "O";
                                BytesPerElement = 2;
                                break;
                            case 0x83:
                            case 0x8c:
                                DataFiles[k].FileType = "I";
                                BytesPerElement = 2;
                                break;
                            case 0x91:
                                DataFiles[k].FileType = "L";
                                //Long Integer
                                BytesPerElement = 4;
                                break;
                            case 0x92:
                                DataFiles[k].FileType = "MG";
                                //Message Command 146
                                BytesPerElement = 50;
                                break;
                            case 0x93:
                                DataFiles[k].FileType = "PD";
                                //PID
                                BytesPerElement = 46;
                                break;
                            case 0x94:
                                DataFiles[k].FileType = "PLS";
                                //Programmable Limit Swith
                                BytesPerElement = 12;

                                break;
                            default:
                                DataFiles[k].FileType = "Undefined";
                                //* 61h=Program File
                                BytesPerElement = 2;
                                break;
                        }
                        DataFiles[k].NumberOfElements = (FileZeroData[FilePosition + 1] + FileZeroData[FilePosition + 2] * 256) / BytesPerElement;
                        DataFiles[k].FileNumber = i;

                        //* Only return valid user data files
                        if (FileZeroData[FilePosition] > 0x81 & FileZeroData[FilePosition] < 0x95)
                            k += 1;

                        //* Index file number once in the region of data files
                        if (k > 0)
                            i += 1;
                        FilePosition += 10;
                    }

                    //* Move to an array with a length of only good data files
                    DataFileDetails[] GoodDataFiles = new DataFileDetails[k];
                    for (int l = 0; l <= k - 1; l++)
                    {
                        GoodDataFiles[l] = DataFiles[l];
                    }

                    return GoodDataFiles;
                }
                else
                {
                    throw new DF1Exception(DecodeMessage(reply) + " - Failed to get data table list");
                }
            }
            else
            {
                throw new DF1Exception(DecodeMessage(reply) + " - Failed to get data table list");
            }
        }

        private byte[] ReadFileDirectory()
        {
            GetProcessorType();

            //*****************************************************
            //* 1 & 2) Get the size of the File Directory
            //*****************************************************
            ParsedDataAddress PAddress = default(ParsedDataAddress);
            switch (ProcessorType)
            {
                case 0x25:
                case 0x58:
                    //* SLC 5/02 or ML1000
                    PAddress.FileType = 0;
                    PAddress.Element = 0x23;
                    break;
                case 0x88:
                case 0x89:
                case 0x8c:
                case 0x9c:
                    //* ML1100, ML1200, ML1500
                    PAddress.FileType = 2;
                    PAddress.Element = 0x2f;
                    break;
                default:
                    //* SLC 5/04, SLC 5/05
                    PAddress.FileType = 1;
                    PAddress.Element = 0x23;
                    break;
            }

            int reply = 0;

            byte[] data = ReadRawData(PAddress, 2, ref reply);
            if (reply != 0)
                throw new DF1Exception("Failed to Get Program Directory Size- " + DecodeMessage(reply));


            //*****************************************************
            //* 3) Read All of File 0 (File Directory)
            //*****************************************************
            PAddress.Element = 0;
            int FileZeroSize = data[0] + data[1] * 256;
            byte[] FileZeroData = ReadRawData(PAddress, FileZeroSize, ref reply);
            if (reply != 0)
                throw new DF1Exception("Failed to Get Program Directory - " + DecodeMessage(reply));

            return FileZeroData;
        }
        //********************************************************************
        //* Retreive the ladder files
        //* This was developed from a combination of Chapter 12
        //*  and reverse engineering
        //********************************************************************
        public struct PLCFileDetails
        {
            public int FileType;
            public int FileNumber;
            public int NumberOfBytes;
            public byte[] data;
        }
        public System.Collections.ObjectModel.Collection<PLCFileDetails> UploadProgramData()
        {
            //'*****************************************************
            //'* 1,2 & 3) Read all of the directory File
            //'*****************************************************
            byte[] FileZeroData = ReadFileDirectory();

            ParsedDataAddress PAddress = default(ParsedDataAddress);
            int reply = 0;

            if (UploadProgress != null)
            {
                UploadProgress(this, System.EventArgs.Empty);
            }

            //**************************************************
            //* 4) Parse the data from the File Directory data
            //**************************************************
            //*********************************************************************************
            //* Starting at corresponding File Position, each program is defined with 10 bytes
            //* 1st byte=File Type
            //* 2nd & 3rd bytes are program size
            //* 4th & 5th bytes are location with memory
            //*********************************************************************************
            int FilePosition = 0;
            PLCFileDetails ProgramFile = new PLCFileDetails();
            System.Collections.ObjectModel.Collection<PLCFileDetails> ProgramFiles = new System.Collections.ObjectModel.Collection<PLCFileDetails>();

            //*********************************************
            //* 4a) Add the directory information
            //*********************************************
            ProgramFile.FileNumber = 0;
            ProgramFile.data = FileZeroData;
            ProgramFile.FileType = PAddress.FileType;
            ProgramFile.NumberOfBytes = FileZeroData.Length;
            ProgramFiles.Add(ProgramFile);

            //**********************************************
            //* 5) Read the rest of the data tables
            //**********************************************
            int DataFileGroup = 0;
            int ForceFileGroup = 0;
            int SystemFileGroup = 0;
            int SystemLadderFileGroup = 0;
            int LadderFileGroup = 0;
            int Unknown1FileGroup = 0;
            int Unknown2FileGroup = 0;
            if (reply == 0)
            {
                int NumberOfProgramFiles = FileZeroData[46] + FileZeroData[47] * 256;

                //* Comb through data file 0 and get the program file details
                int i = 0;
                //* The start of program file definitions
                switch (ProcessorType)
                {
                    case 0x25:
                    case 0x58:
                        FilePosition = 93;
                        break;
                    case 0x88:
                    case 0x89:
                    case 0x8c:
                    case 0x9c:
                        FilePosition = 103;
                        break;
                    default:
                        FilePosition = 79;
                        break;
                }

                while (FilePosition < FileZeroData.Length & reply == 0)
                {
                    ProgramFile.FileType = FileZeroData[FilePosition];
                    ProgramFile.NumberOfBytes = (FileZeroData[FilePosition + 1] + FileZeroData[FilePosition + 2] * 256);

                    if (ProgramFile.FileType >= 0x40 && ProgramFile.FileType <= 0x5f)
                    {
                        ProgramFile.FileNumber = SystemFileGroup;
                        SystemFileGroup += 1;
                    }
                    if ((ProgramFile.FileType >= 0x20 && ProgramFile.FileType <= 0x3f))
                    {
                        ProgramFile.FileNumber = LadderFileGroup;
                        LadderFileGroup += 1;
                    }
                    if ((ProgramFile.FileType >= 0x60 && ProgramFile.FileType <= 0x7f))
                    {
                        ProgramFile.FileNumber = SystemLadderFileGroup;
                        SystemLadderFileGroup += 1;
                    }
                    if (ProgramFile.FileType >= 0x80 && ProgramFile.FileType <= 0x9f)
                    {
                        ProgramFile.FileNumber = DataFileGroup;
                        DataFileGroup += 1;
                    }
                    if (ProgramFile.FileType >= 0xa0 && ProgramFile.FileType <= 0xbf)
                    {
                        ProgramFile.FileNumber = ForceFileGroup;
                        ForceFileGroup += 1;
                    }
                    if (ProgramFile.FileType >= 0xc0 && ProgramFile.FileType <= 0xdf)
                    {
                        ProgramFile.FileNumber = Unknown1FileGroup;
                        Unknown1FileGroup += 1;
                    }
                    if (ProgramFile.FileType >= 0xe0 && ProgramFile.FileType <= 0xff)
                    {
                        ProgramFile.FileNumber = Unknown2FileGroup;
                        Unknown2FileGroup += 1;
                    }

                    PAddress.FileType = ProgramFile.FileType;
                    PAddress.FileNumber = ProgramFile.FileNumber;

                    if (ProgramFile.NumberOfBytes > 0)
                    {
                        ProgramFile.data = ReadRawData(PAddress, ProgramFile.NumberOfBytes, ref reply);
                        if (reply != 0)
                            throw new DF1Exception("Failed to Read Program File " + PAddress.FileNumber + ", Type " + PAddress.FileType + " - " + DecodeMessage(reply));
                    }
                    else
                    {
                        byte[] ZeroLengthData = new byte[-1 + 1];
                        ProgramFile.data = ZeroLengthData;
                    }


                    ProgramFiles.Add(ProgramFile);
                    if (UploadProgress != null)
                    {
                        UploadProgress(this, System.EventArgs.Empty);
                    }

                    i += 1;
                    //* 10 elements are used to define each program file
                    //* SLC 5/02 or ML1000
                    if (ProcessorType == 0x25 || ProcessorType == 0x58)
                    {
                        FilePosition += 8;
                    }
                    else
                    {
                        FilePosition += 10;
                    }
                }

            }

            return ProgramFiles;
        }

        //****************************************************************
        //* Download a group of files defined in the PLCFiles Collection
        //****************************************************************
        public void DownloadProgramData(System.Collections.ObjectModel.Collection<PLCFileDetails> PLCFiles)
        {
            //******************************
            //* 1 & 2) Change to program Mode
            //******************************
            SetProgramMode();
            if (DownloadProgress != null)
            {
                DownloadProgress(this, System.EventArgs.Empty);
            }

            //*************************************************************************
            //* 2) Initialize Memory & Put in Download mode using Execute Command List
            //*************************************************************************
            int DataLength = 0;
            switch (ProcessorType)
            {
                case 0x5b:
                case 0x78:
                    DataLength = 13;
                    break;
                case 0x88:
                case 0x89:
                case 0x8c:
                case 0x9c:
                    DataLength = 15;
                    break;
                default:
                    DataLength = 15;
                    break;
            }

            byte[] data = new byte[DataLength + 1];
            //* 2 commands
            data[0] = 0x2;
            //* Number of bytes in 1st command
            data[1] = 0xa;
            //* Function &HAA
            data[2] = 0xaa;
            //* Write 4 bytes
            data[3] = 4;
            data[4] = 0;
            //* File type 63
            data[5] = 0x63;

            //* Lets go ahead and setup the file type for later use
            ParsedDataAddress PAddress = default(ParsedDataAddress);
            int reply = 0;

            //**********************************
            //* 2a) Search for File 0, Type 24
            //**********************************
            int i = 0;
            while (i < PLCFiles.Count && (PLCFiles[i].FileNumber != 0 || PLCFiles[i].FileType != 0x24))
            {
                i += 1;
            }

            //* Write bytes 02-07 from File 0, Type 24 to File 0, Type 63
            if (i < PLCFiles.Count)
            {
                data[8] = PLCFiles[i].data[2];
                data[9] = PLCFiles[i].data[3];
                data[10] = PLCFiles[i].data[4];
                data[11] = PLCFiles[i].data[5];
                if (DataLength > 14)
                {
                    data[12] = PLCFiles[i].data[6];
                    data[13] = PLCFiles[i].data[7];
                }
            }


            switch (ProcessorType)
            {
                case 0x78:
                case 0x5b:
                case 0x49:
                    //* SLC 5/05, 5/04, 5/03
                    //* Read these 4 bytes to write back, File 0, Type 63
                    PAddress.FileType = 0x63;
                    PAddress.Element = 0;
                    PAddress.SubElement = 0;
                    byte[] FourBytes = ReadRawData(PAddress, 4, ref reply);
                    if (reply == 0)
                    {
                        Array.Copy(FourBytes, 0, data, 8, 4);
                        PAddress.FileType = 1;
                        PAddress.Element = 0x23;
                    }
                    else
                    {
                        throw new DF1Exception("Failed to Read File 0, Type 63h - " + DecodeMessage(reply));
                    }

                    //* Number of bytes in 1st command
                    data[1] = 0xa;
                    //* Number of bytes to write
                    data[3] = 4;
                    break;
                case 0x88:
                case 0x89:
                case 0x8c:
                case 0x9c:
                    //* ML1200, ML1500, ML1100
                    //* Number of bytes in 1st command
                    data[1] = 0xc;
                    //* Number of bytes to write
                    data[3] = 6;
                    PAddress.FileType = 2;
                    PAddress.Element = 0x23;
                    break;
                default:
                    //* Fill in the gap for an unknown processor
                    data[1] = 0xa;
                    data[3] = 4;
                    PAddress.FileType = 1;
                    PAddress.Element = 0x23;
                    break;
            }


            //* 1 byte in 2nd command - Start Download
            data[data.Length - 2] = 1;
            data[data.Length - 1] = 0x56;

            int rTNS = 0;
            reply = PrefixAndSend(0xf, 0x88, data, true, ref rTNS);
            if (reply != 0)
                throw new DF1Exception("Failed to Initialize for Download - " + DecodeMessage(reply));
            if (DownloadProgress != null)
            {
                DownloadProgress(this, System.EventArgs.Empty);
            }


            //*********************************
            //* 4) Secure Sole Access
            //*********************************
            byte[] data2 = new byte[-1 + 1];
            reply = PrefixAndSend(0xf, 0x11, data2, true, ref rTNS);
            if (reply != 0)
                throw new DF1Exception("Failed to Secure Sole Access - " + DecodeMessage(reply));
            if (DownloadProgress != null)
            {
                DownloadProgress(this, System.EventArgs.Empty);
            }

            //*********************************
            //* 5) Write the directory length
            //*********************************
            PAddress.BitNumber = 16;
            byte[] data3 = new byte[2];
            data3[0] = PLCFiles[0].data.Length & 0xff;
            data3[1] = (PLCFiles[0].data.Length - data3[0]) / 256;
            reply = WriteRawData(PAddress, 2, data3);
            if (reply != 0)
                throw new DF1Exception("Failed to Write Directory Length - " + DecodeMessage(reply));
            if (DownloadProgress != null)
            {
                DownloadProgress(this, System.EventArgs.Empty);
            }

            //*********************************
            //* 6) Write program directory
            //*********************************
            PAddress.Element = 0;
            reply = WriteRawData(PAddress, PLCFiles[0].data.Length, PLCFiles[0].data);
            if (reply != 0)
                throw new DF1Exception("Failed to Write New Program Directory - " + DecodeMessage(reply));
            if (DownloadProgress != null)
            {
                DownloadProgress(this, System.EventArgs.Empty);
            }

            //*********************************
            //* 7) Write Program & Data Files
            //*********************************
            for (i = 1; i <= PLCFiles.Count - 1; i++)
            {
                PAddress.FileNumber = PLCFiles[i].FileNumber;
                PAddress.FileType = PLCFiles[i].FileType;
                PAddress.Element = 0;
                PAddress.SubElement = 0;
                PAddress.BitNumber = 16;
                reply = WriteRawData(PAddress, PLCFiles[i].data.Length, PLCFiles[i].data);
                if (reply != 0)
                    throw new DF1Exception("Failed when writing files to PLC - " + DecodeMessage(reply));
                if (DownloadProgress != null)
                {
                    DownloadProgress(this, System.EventArgs.Empty);
                }
            }

            //*********************************
            //* 8) Complete the Download
            //*********************************
            reply = PrefixAndSend(0xf, 0x52, data2, true, ref rTNS);
            if (reply != 0)
                throw new DF1Exception("Failed to Indicate to PLC that Download is complete - " + DecodeMessage(reply));
            if (DownloadProgress != null)
            {
                DownloadProgress(this, System.EventArgs.Empty);
            }

            //*********************************
            //* 9) Release Sole Access
            //*********************************
            reply = PrefixAndSend(0xf, 0x12, data2, true, ref rTNS);
            if (reply != 0)
                throw new DF1Exception("Failed to Release Sole Access - " + DecodeMessage(reply));
            if (DownloadProgress != null)
            {
                DownloadProgress(this, System.EventArgs.Empty);
            }
        }


        /// <summary>
        /// Get the number of slots in the rack
        /// </summary>
        /// <returns></returns>
        /// <remarks></remarks>
        public int GetSlotCount()
        {
            //* Get the header of the data table definition file
            byte[] data = new byte[5];

            //* Number of bytes to read
            data[0] = 4;
            //* Data File Number (0 is the system file)
            data[1] = 0;
            //* File Type (&H60 must be a system type), this was pulled from reverse engineering
            data[2] = 0x60;
            //* Element Number
            data[3] = 0;
            //* Sub Element Offset in words
            data[4] = 0;


            int rTNS = 0;
            int reply = PrefixAndSend(0xf, 0xa2, data, true, ref rTNS);

            if (reply == 0)
            {
                if (DataPackets[rTNS][6] > 0)
                {
                    return DataPackets[rTNS][6] - 1;
                    //* if a rack based system, then subtract processor slot
                }
                else
                {
                    return 0;
                    //* micrologix reports 0 slots
                }
            }
            else
            {
                throw new DF1Exception("Failed to Release Sole Access - " + DecodeMessage(reply));
            }
        }

        public struct IOConfig
        {
            public int InputBytes;
            public int OutputBytes;
            public int CardCode;
        }
        /// <summary>
        /// Get IO card list currently in rack
        /// </summary>
        /// <returns></returns>
        /// <remarks></remarks>
        public IOConfig[] GetIOConfig()
        {
            int ProcessorType = GetProcessorType();


            //* Is it a Micrologix 1500?
            if (ProcessorType == 0x89 | ProcessorType == 0x8c)
            {
                return GetML1500IOConfig();
            }
            else
            {
                return GetSLCIOConfig();
            }
        }

        /// <summary>
        /// Get IO card list currently in rack of a SLC
        /// </summary>
        /// <returns></returns>
        /// <remarks></remarks>
        public IOConfig[] GetSLCIOConfig()
        {
            int slots = GetSlotCount();

            if (slots > 0)
            {
                //* Get the header of the data table definition file
                byte[] data = new byte[5];

                //* Number of bytes to read
                data[0] = 4 + (slots + 1) * 6 + 2;
                //* Data File Number (0 is the system file)
                data[1] = 0;
                //* File Type (&H60 must be a system type), this was pulled from reverse engineering
                data[2] = 0x60;
                //* Element Number
                data[3] = 0;
                //* Sub Element Offset in words
                data[4] = 0;


                int rTNS = 0;
                int reply = PrefixAndSend(0xf, 0xa2, data, true, ref rTNS);

                byte[] BytesForConverting = new byte[2];
                IOConfig[] IOResult = new IOConfig[slots + 1];
                if (reply == 0)
                {
                    //* Extract IO information
                    for (int i = 0; i <= slots; i++)
                    {
                        IOResult[i].InputBytes = DataPackets[rTNS][i * 6 + 10];
                        IOResult[i].OutputBytes = DataPackets[rTNS][i * 6 + 12];
                        BytesForConverting[0] = DataPackets[rTNS][i * 6 + 14];
                        BytesForConverting[1] = DataPackets[rTNS][i * 6 + 15];
                        IOResult[i].CardCode = BitConverter.ToInt16(BytesForConverting, 0);
                    }
                    return IOResult;
                }
                else
                {
                    throw new DF1Exception("Failed to get IO Config - " + DecodeMessage(reply));
                }
            }
            else
            {
                throw new DF1Exception("Failed to get Slot Count");
            }
        }


        /// <summary>
        /// Get IO card list currently in rack of a ML1500
        /// </summary>
        /// <returns></returns>
        /// <remarks></remarks>
        public IOConfig[] GetML1500IOConfig()
        {
            //*************************************************************************
            //* Read the first 4 bytes of File 0, type 62 to get the total file length
            //**************************************************************************
            byte[] data = new byte[5];
            int rTNS = 0;

            //* Number of bytes to read
            data[0] = 4;
            //* Data File Number (0 is the system file)
            data[1] = 0;
            //* File Type (&H62 must be a system type), this was pulled from reverse engineering
            data[2] = 0x62;
            //* Element Number
            data[3] = 0;
            //* Sub Element Offset in words
            data[4] = 0;

            int reply = PrefixAndSend(0xf, 0xa2, data, true, ref rTNS);

            //******************************************
            //* Read all of File Zero, Type 62
            //******************************************
            if (reply == 0)
            {
                //TODO: Get this corrected
                int FileZeroSize = DataPackets[rTNS][6] * 2;
                byte[] FileZeroData = new byte[FileZeroSize + 1];
                int FilePosition = 0;
                int Subelement = 0;
                int i = 0;

                //* Number of bytes to read
                if (FileZeroSize > 0x50)
                {
                    data[0] = 0x50;
                }
                else
                {
                    data[0] = FileZeroSize;
                }

                //* Loop through reading all of file 0 in chunks of 80 bytes

                while (FilePosition < FileZeroSize & reply == 0)
                {
                    //* Read the file
                    reply = PrefixAndSend(0xf, 0xa2, data, true, ref rTNS);

                    //* Transfer block of data read to the data table array
                    i = 0;
                    while (i < data[0])
                    {
                        FileZeroData[FilePosition] = DataPackets[rTNS][i + 6];
                        i += 1;
                        FilePosition += 1;
                    }


                    //* point to the next element, by taking the last Start Element(in words) and adding it to the number of bytes read
                    Subelement += data[0] / 2;
                    if (Subelement < 255)
                    {
                        data[3] = Subelement;
                    }
                    else
                    {
                        //* Use extended addressing
                        if (data.Length < 6)
                            Array.Resize(ref data, 6);
                        data[5] = Math.Floor(Subelement / 256);
                        //* 256+data[5]
                        data[4] = Subelement - (data[5] * 256);
                        //*  calculate offset
                        data[3] = 255;
                    }

                    //* Set next length of data to read. Max of 80
                    if (FileZeroSize - FilePosition < 80)
                    {
                        data[0] = FileZeroSize - FilePosition;
                    }
                    else
                    {
                        data[0] = 80;
                    }
                }


                //**********************************
                //* Extract the data from the file
                //**********************************
                if (reply == 0)
                {
                    int SlotCount = FileZeroData[2] - 2;
                    if (SlotCount < 0)
                        SlotCount = 0;
                    int SlotIndex = 1;
                    IOConfig[] IOResult = new IOConfig[SlotCount + 1];

                    //*Start getting slot data
                    i = 32 + SlotCount * 4;
                    byte[] BytesForConverting = new byte[2];

                    while (SlotIndex <= SlotCount)
                    {
                        IOResult[SlotIndex].InputBytes = FileZeroData[i + 2] * 2;
                        IOResult[SlotIndex].OutputBytes = FileZeroData[i + 8] * 2;
                        BytesForConverting[0] = FileZeroData[i + 18];
                        BytesForConverting[1] = FileZeroData[i + 19];
                        IOResult[SlotIndex].CardCode = BitConverter.ToInt16(BytesForConverting, 0);

                        i += 26;
                        SlotIndex += 1;
                    }


                    //****************************************
                    //* Get the Slot 0(base unit) information
                    //****************************************
                    data[0] = 8;
                    //* Data File Number (0 is the system file)
                    data[1] = 0;
                    //* File Type (&H60 must be a system type), this was pulled from reverse engineering
                    data[2] = 0x60;
                    //* Element Number
                    data[3] = 0;
                    //* Sub Element Offset in words
                    data[4] = 0;


                    //* Read File 0 to get the IO count on the base unit
                    reply = PrefixAndSend(0xf, 0xa2, data, true, ref rTNS);

                    if (reply == 0)
                    {
                        IOResult[0].InputBytes = DataPackets[rTNS][10];
                        IOResult[0].OutputBytes = DataPackets[rTNS][12];
                    }
                    else
                    {
                        throw new DF1Exception("Failed to get Base IO Config for Micrologix 1500- " + DecodeMessage(reply));
                    }


                    return IOResult;
                }
            }

            throw new DF1Exception("Failed to get IO Config for Micrologix 1500- " + DecodeMessage(reply));
        }


        //***************************************************************
        //* This method is intended to make it easy to configure the
        //* comm port settings. It is similar to the auto configure
        //* in RSLinx.
        //* It uses the echo command and sends the character "A", then
        //* checks if it received a response.
        //**************************************************************
        /// <summary>
        /// This method is intended to make it easy to configure the
        /// comm port settings. It is similar to the auto configure
        /// in RSLinx. A successful configuration returns a 0 and sets the
        /// properties to the discovered values.
        /// It will fire the event "AutoDetectTry" for each setting attempt
        /// It uses the echo command and sends the character "A", then
        /// checks if it received a response.
        /// </summary>
        /// <returns></returns>
        /// <remarks></remarks>
        public int DetectCommSettings()
        {
            //Dim rTNS As Integer

            byte[] data = { 65 };
            int[] BaudRates = {
            38400,
            19200,
            9600
        };
            int BRIndex = 0;
            System.IO.Ports.Parity[] Parities = {
            System.IO.Ports.Parity.None,
            System.IO.Ports.Parity.Even
        };
            int PIndex = 0;
            CheckSumOptions[] Checksums = {
            CheckSumOptions.Crc,
            CheckSumOptions.Bcc
        };
            int CSIndex = 0;
            int reply = -1;

            DisableEvent = true;
            //* We are sending a small amount of data, so speed up the response
            MaxTicks = 3;
            while (BRIndex < BaudRates.Length & reply != 0)
            {
                PIndex = 0;
                while (PIndex < Parities.Length & reply != 0)
                {
                    CSIndex = 0;
                    while (CSIndex < Checksums.Length & reply != 0)
                    {
                        CloseComms();
                        m_BaudRate = BaudRates[BRIndex];
                        m_Parity = Parities[PIndex];
                        m_CheckSum = Checksums[CSIndex];

                        if (AutoDetectTry != null)
                        {
                            AutoDetectTry(this, System.EventArgs.Empty);
                        }

                        //* send an "A" and look for echo back
                        //reply = PrefixAndSend(&H6, &H0, data, True, rTNS)

                        //* Send an ENQ sequence until we get a reply
                        reply = SendENQ();

                        //* If port cannot be opened, do not retry
                        if (reply == -6)
                            return reply;

                        MaxTicks += 1;
                        CSIndex += 1;
                    }
                    PIndex += 1;
                }
                BRIndex += 1;
            }

            DisableEvent = false;
            MaxTicks = 100;
            return reply;
        }

        //******************************************
        //* Synchronous read of any data type
        //*  this function does not declare its return type because it dependent on the data type read
        //******************************************
        /// <summary>
        /// Synchronous read of any data type
        /// this function returns results as an array of strings
        /// </summary>
        /// <param name="startAddress"></param>
        /// <param name="numberOfElements"></param>
        /// <returns></returns>
        /// <remarks></remarks>
        public string[] ReadAny(string startAddress, int numberOfElements)
        {
            //* Limit number of elements to one complete file or 256 elements
            //* NOT TRUE
            //If numberOfElements > 256 Then
            //Throw New ApplicationException("Can not read more than 256 elements")
            //numberOfElements = 256
            //End If

            byte[] data = new byte[5];
            ParsedDataAddress ParsedResult = ParseAddress(startAddress);

            //* Invalid address?
            if (ParsedResult.FileType == 0)
            {
                throw new DF1Exception("Invalid Address");
            }


            //* If requesting 0 elements, then default to 1
            Int16 ArrayElements = numberOfElements - 1;
            if (ArrayElements < 0)
            {
                ArrayElements = 0;
            }

            //* If reading at bit level ,then convert number bits to read to number of words
            if (ParsedResult.BitNumber < 16)
            {
                ArrayElements = Math.Floor(numberOfElements / 16);
                if (ArrayElements % 16 > 0)
                    data[0] += 1;
            }


            //* Number of bytes to read
            int NumberOfBytes = 0;
            //NumberOfBytes = ((ArrayElements + 1)) * 2

            switch (ParsedResult.FileType)
            {
                case 0x8d:
                    NumberOfBytes = ((ArrayElements + 1)) * 82;
                    //* String
                    break;
                case 0x8a:
                    NumberOfBytes = ((ArrayElements + 1)) * 4;
                    //* Float
                    break;
                case 0x91:
                    NumberOfBytes = ((ArrayElements + 1)) * 4;
                    //* Long
                    break;
                case 0x92:
                    NumberOfBytes = ((ArrayElements + 1)) * 50;
                    //* Message
                    break;
                case 0x86:
                case 0x87:
                    NumberOfBytes = ((ArrayElements + 1)) * 2;
                    //* Timer
                    break;
                //ArrayElements = (ArrayElements + 1) * 3 - 1
                default:
                    NumberOfBytes = ((ArrayElements + 1)) * 2;
                    break;
            }


            //* If it is a multiple read of sub-elements of timers and counter, then read an array of the same consectutive sub element
            if (ParsedResult.SubElement > 0 && (ParsedResult.FileType == 0x86 | ParsedResult.FileType == 0x87))
            {
                NumberOfBytes = (NumberOfBytes * 3) - 4;
                //* There are 3 words per sub element (6 bytes)
            }


            int reply = 0;
            byte[] ReturnedData = new byte[NumberOfBytes + 1];
            int ReturnedDataIndex = 0;

            int BytesToRead = 0;

            int Retries = 0;
            while (reply == 0 && ReturnedDataIndex < NumberOfBytes)
            {
                BytesToRead = NumberOfBytes;

                byte[] ReturnedData2 = new byte[BytesToRead + 1];
                ReturnedData2 = ReadRawData(ParsedResult, BytesToRead, ref reply);

                //* Point to next set of bytes to read in block
                if (reply == 0)
                {
                    //* Point to next element to begin reading
                    ReturnedData2.CopyTo(ReturnedData, ReturnedDataIndex);
                    ReturnedDataIndex += BytesToRead;

                }
                else if (Retries < 2)
                {
                    Retries += 1;
                    reply = 0;
                }
                else
                {
                    //* An error was returned from the read operation
                    throw new DF1Exception(DecodeMessage(reply));
                }
            }


            //If reply = 0 Then
            //***************************************************
            //* Extract returned data into appropriate data type
            //***************************************************
            string[] result = new string[ArrayElements + 1];
            int StringLength = 0;
            //Dim StringResult(ArrayElements) As String
            switch (ParsedResult.FileType)
            {
                case 0x8a:
                    //* Floating point read (&H8A)
                    //Dim result(ArrayElements) As Single
                    for (int i = 0; i <= ArrayElements; i++)
                    {
                        result[i] = BitConverter.ToSingle(ReturnedData, (i * 4));
                    }

                    break;
                case 0x8d:
                    // * String
                    //Dim result(ArrayElements) As String
                    for (int i = 0; i <= ArrayElements; i++)
                    {
                        //result(i) = BitConverter.ToString(ReturnedData, 2, StringLength)
                        StringLength = BitConverter.ToInt16(ReturnedData, (i * 84));
                        //* The controller may falsely report the string length, so set to max allowed
                        if (StringLength > 82)
                            StringLength = 82;

                        //* use a string builder for increased performance
                        System.Text.StringBuilder result2 = new System.Text.StringBuilder();
                        int j = 2;
                        //* Stop concatenation if a zero (NULL) is reached
                        while (j < StringLength + 2 & ReturnedData[(i * 84) + j + 1] > 0)
                        {
                            result2.Append(Strings.Chr(ReturnedData[(i * 84) + j + 1]));
                            //* Prevent an odd length string from getting a Null added on
                            if (j < StringLength + 1 & (ReturnedData[(i * 84) + j]) > 0)
                                result2.Append(Strings.Chr(ReturnedData[(i * 84) + j]));
                            j += 2;
                        }
                        result[i] = result2.ToString();
                    }

                    break;
                case 0x86:
                case 0x87:
                    //* Timer, counter
                    //* If a sub element is designated then read the same sub element for all timers
                    int j = 0;
                    for (int i = 0; i <= ArrayElements; i++)
                    {
                        if (ParsedResult.SubElement > 0)
                        {
                            j = i * 6;
                        }
                        else
                        {
                            j = i * 2;
                        }
                        result[i] = BitConverter.ToInt16(ReturnedData, (j));
                    }

                    break;
                case 0x91:
                    //* Long Value read (&H91)
                    //Dim result(ArrayElements) As Single
                    for (int i = 0; i <= ArrayElements; i++)
                    {
                        result[i] = BitConverter.ToInt32(ReturnedData, (i * 4));
                    }

                    break;
                case 0x92:
                    //* MSG Value read (&H92)
                    //Dim result(ArrayElements) As Single
                    for (int i = 0; i <= ArrayElements; i++)
                    {
                        result[i] = BitConverter.ToString(ReturnedData, (i * 50), 50);
                    }

                    break;
                default:
                    //Dim result(ArrayElements) As Int16
                    for (int i = 0; i <= ArrayElements; i++)
                    {
                        result[i] = BitConverter.ToInt16(ReturnedData, (i * 2));
                    }

                    break;
            }
            //End If


            //******************************************************************************
            //* If the number of words to read is not specified, then return a single value
            //******************************************************************************
            //* Is it a bit level and N or B file?
            if (ParsedResult.BitNumber >= 0 & ParsedResult.BitNumber < 16)
            {
                string[] BitResult = new string[numberOfElements];
                int BitPos = ParsedResult.BitNumber;
                int WordPos = 0;
                //Dim Result(ArrayElements) As Boolean
                //* Set array of consectutive bits
                for (int i = 0; i <= numberOfElements - 1; i++)
                {
                    BitResult[i] = Convert.ToBoolean(result[WordPos] & Math.Pow(2, BitPos));
                    BitPos += 1;
                    if (BitPos > 15)
                    {
                        BitPos = 0;
                        WordPos += 1;
                    }
                }
                return BitResult;
            }

            return result;

            //* An error must have occurred if it made it this far, so throw exception
            //Throw New DF1Exception(DecodeMessage(reply))
        }
        //*************************************************************
        //* Overloaded method of ReadAny - that reads only one element
        //*************************************************************
        /// <summary>
        /// Synchronous read of any data type
        /// this function returns results as a string
        /// </summary>
        /// <param name="startAddress"></param>
        /// <returns></returns>
        /// <remarks></remarks>
        public string ReadAny(string startAddress)
        {
            return ReadAny(startAddress, 1)[0];
        }

        /// <summary>
        /// Reads values and returns them as integers
        /// </summary>
        /// <param name="startAddress"></param>
        /// <param name="numberOfBytes"></param>
        /// <returns></returns>
        /// <remarks></remarks>
        public int[] ReadInt(string startAddress, int numberOfBytes)
        {
            string[] result = null;
            result = ReadAny(startAddress, numberOfBytes);

            int[] Ints = new int[result.Length + 1];
            for (int i = 0; i <= result.Length - 1; i++)
            {
                Ints[i] = Convert.ToInt32(result[i]);
            }

            return Ints;
        }

        //*********************************************************************************
        //* Read Raw File data and break up into chunks because of limits of DF1 protocol
        //*********************************************************************************
        private byte[] ReadRawData(ParsedDataAddress PAddress, int numberOfBytes, ref int reply)
        {
            int NumberOfBytesToRead = 0;
            int FilePosition = 0;
            int rTNS = 0;
            byte[] ResultData = new byte[numberOfBytes];

            while (FilePosition < numberOfBytes && reply == 0)
            {
                //* Set next length of data to read. Max of 236 (slc 5/03 and up)
                //* This must limit to 82 for 5/02 and below
                if (numberOfBytes - FilePosition < 236)
                {
                    NumberOfBytesToRead = numberOfBytes - FilePosition;
                }
                else
                {
                    NumberOfBytesToRead = 236;
                }

                //* String is an exception
                if (NumberOfBytesToRead > 168 && PAddress.FileType == 0x8d)
                {
                    //* Only two string elements can be read on each read (168 bytes)
                    NumberOfBytesToRead = 168;
                }

                if (NumberOfBytesToRead > 234 && (PAddress.FileType == 0x86 || PAddress.FileType == 0x87))
                {
                    //* Timers & counters read in multiples of 6 bytes
                    NumberOfBytesToRead = 234;
                }

                //* Data Monitor File is an exception
                if (NumberOfBytesToRead > 0x78 && PAddress.FileType == 0xa4)
                {
                    //* Only two string elements can be read on each read (168 bytes)
                    NumberOfBytesToRead = 0x78;
                }

                //* The SLC 5/02 can only read &H50 bytes per read, possibly the ML1500
                //If NumberOfBytesToRead > &H50 AndAlso (ProcessorType = &H25 Or ProcessorType = &H89) Then
                if (NumberOfBytesToRead > 0x50 && (ProcessorType == 0x25))
                {
                    NumberOfBytesToRead = 0x50;
                }

                if (NumberOfBytesToRead > 0)
                {
                    int DataSize = 0;
                    int Func = 0;

                    if (PAddress.SubElement == 0)
                    {
                        DataSize = 3;
                        Func = 0xa1;
                    }
                    else
                    {
                        DataSize = 4;
                        Func = 0xa2;
                    }

                    //* Check if we need extended addressing
                    if (PAddress.Element >= 255)
                        DataSize += 2;
                    if (PAddress.SubElement >= 255)
                        DataSize += 2;

                    byte[] data = new byte[DataSize + 1];


                    //* Number of bytes to read - 
                    data[0] = NumberOfBytesToRead;

                    //* File Number
                    data[1] = PAddress.FileNumber;

                    //* File Type
                    data[2] = PAddress.FileType;

                    //* Starting Element Number
                    //* point to the next element (ref page 7-17)
                    if (PAddress.Element < 255)
                    {
                        data[3] = PAddress.Element;
                    }
                    else
                    {
                        //* Use extended addressing
                        data[5] = Math.Floor(PAddress.Element / 256);
                        //* 256+data[5]
                        data[4] = PAddress.Element - (data[5] * 256);
                        //*  calculate offset
                        data[3] = 255;
                    }

                    //* Sub Element (Are we using the subelement function of &HA2?)
                    if (Func == 0xa2)
                    {
                        //* point to the next element (ref page 7-17)
                        if (PAddress.SubElement < 255)
                        {
                            data[data.Length - 1] = PAddress.SubElement;
                        }
                        else
                        {
                            //* Use extended addressing
                            data[data.Length - 1] = Math.Floor(PAddress.SubElement / 256);
                            data[data.Length - 2] = PAddress.SubElement - (data[data.Length - 1] * 256);
                            data[data.Length - 3] = 255;
                        }
                    }


                    reply = PrefixAndSend(0xf, Func, data, true, ref rTNS);

                    //***************************************************
                    //* Extract returned data into appropriate data type
                    //* Transfer block of data read to the data table array
                    //***************************************************
                    //* TODO: Check array bounds
                    //Array.Copy(data, 6, ResultData, FilePosition, NumberOfBytesToRead)
                    if (reply == 0)
                    {
                        for (int i = 0; i <= NumberOfBytesToRead - 1; i++)
                        {
                            ResultData[FilePosition + i] = DataPackets[rTNS][i + 6];
                        }
                    }

                    FilePosition += NumberOfBytesToRead;

                    //* point to the next element
                    if (PAddress.FileType == 0xa4)
                    {
                        PAddress.Element += NumberOfBytesToRead / 0x28;
                    }
                    else
                    {
                        //* Use subelement because it works with all data file types
                        PAddress.SubElement += NumberOfBytesToRead / 2;
                    }
                }
            }

            return ResultData;
        }



        //*****************************************************************
        //* Write Section
        //*
        //* Address is in the form of <file type><file Number>:<offset>
        //* examples  N7:0, B3:0,
        //******************************************************************

        //* Handle one value of Integer type
        /// <summary>
        /// Write a single integer value to a PLC data table
        /// The startAddress is in the common form of AB addressing (e.g. N7:0)
        /// </summary>
        /// <param name="startAddress"></param>
        /// <param name="dataToWrite"></param>
        /// <returns></returns>
        /// <remarks></remarks>
        public string WriteData(string startAddress, int dataToWrite)
        {
            int[] temp = new int[2];
            temp[0] = dataToWrite;
            return WriteData(startAddress, 1, temp);
        }


        //* Write an array of integers
        /// <summary>
        /// Write multiple consectutive integer values to a PLC data table
        /// The startAddress is in the common form of AB addressing (e.g. N7:0)
        /// </summary>
        /// <param name="startAddress"></param>
        /// <param name="numberOfElements"></param>
        /// <param name="dataToWrite"></param>
        /// <returns></returns>
        /// <remarks></remarks>
        public int WriteData(string startAddress, int numberOfElements, int[] dataToWrite)
        {
            ParsedDataAddress ParsedResult = ParseAddress(startAddress);

            byte[] ConvertedData = new byte[numberOfElements * ParsedResult.BytesPerElements + 1];

            int i = 0;
            if (ParsedResult.FileType == 0x91)
            {
                //* Write to a Long integer file
                while (i < numberOfElements)
                {
                    //******* NOT Necesary to validate because dataToWrite keeps it in range for a long
                    byte[] b = new byte[4];
                    b = BitConverter.GetBytes(dataToWrite[i]);

                    ConvertedData[i * 4] = b[0];
                    ConvertedData[i * 4 + 1] = b[1];
                    ConvertedData[i * 4 + 2] = b[2];
                    ConvertedData[i * 4 + 3] = b[3];
                    i += 1;
                }
            }
            else
            {
                while (i < numberOfElements)
                {
                    //* Validate range
                    if (dataToWrite[i] > 32767 | dataToWrite[i] < -32768)
                    {
                        throw new DF1Exception("Integer data out of range, must be between -32768 and 32767");
                    }

                    ConvertedData[i * 2] = Convert.ToByte(dataToWrite[i] & 0xff);
                    ConvertedData[i * 2 + 1] = Convert.ToByte((dataToWrite[i] >> 8) & 0xff);

                    i += 1;
                }
            }

            return WriteRawData(ParsedResult, numberOfElements * ParsedResult.BytesPerElements, ConvertedData);
        }

        //* Handle one value of Single type
        /// <summary>
        /// Write a single floating point value to a data table
        /// The startAddress is in the common form of AB addressing (e.g. F8:0)
        /// </summary>
        /// <param name="startAddress"></param>
        /// <param name="dataToWrite"></param>
        /// <returns></returns>
        /// <remarks></remarks>
        public int WriteData(string startAddress, float dataToWrite)
        {
            float[] temp = new float[2];
            temp[0] = dataToWrite;
            return WriteData(startAddress, 1, temp);
        }

        //* Write an array of Singles
        /// <summary>
        /// Write multiple consectutive floating point values to a PLC data table
        /// The startAddress is in the common form of AB addressing (e.g. F8:0)
        /// </summary>
        /// <param name="startAddress"></param>
        /// <param name="numberOfElements"></param>
        /// <param name="dataToWrite"></param>
        /// <returns></returns>
        /// <remarks></remarks>
        public int WriteData(string startAddress, int numberOfElements, float[] dataToWrite)
        {
            ParsedDataAddress ParsedResult = ParseAddress(startAddress);

            byte[] ConvertedData = new byte[numberOfElements * ParsedResult.BytesPerElements + 1];

            int i = 0;
            if (ParsedResult.FileType == 0x8a)
            {
                //*Write to a floating point file
                byte[] bytes = new byte[5];
                for (i = 0; i <= numberOfElements - 1; i++)
                {
                    bytes = BitConverter.GetBytes(Convert.ToSingle(dataToWrite[i]));
                    for (int j = 0; j <= 3; j++)
                    {
                        ConvertedData[i * 4 + j] = Convert.ToByte(bytes[j]);
                    }
                }
            }
            else if (ParsedResult.FileType == 0x91)
            {
                //* Write to a Long integer file
                while (i < numberOfElements)
                {
                    //* Validate range
                    if (dataToWrite[i] > 2147483647 | dataToWrite[i] < -2147483648L)
                    {
                        throw new DF1Exception("Integer data out of range, must be between -2147483648 and 2147483647");
                    }

                    byte[] b = new byte[4];
                    b = BitConverter.GetBytes(Convert.ToInt32(dataToWrite[i]));

                    ConvertedData[i * 4] = b[0];
                    ConvertedData[i * 4 + 1] = b[1];
                    ConvertedData[i * 4 + 2] = b[2];
                    ConvertedData[i * 4 + 3] = b[3];
                    i += 1;
                }
            }
            else
            {
                //* Write to an integer file
                while (i < numberOfElements)
                {
                    //* Validate range
                    if (dataToWrite[i] > 32767 | dataToWrite[i] < -32768)
                    {
                        throw new DF1Exception("Integer data out of range, must be between -32768 and 32767");
                    }

                    ConvertedData[i * 2] = Convert.ToByte(dataToWrite[i] & 0xff);
                    ConvertedData[i * 2 + 1] = Convert.ToByte((dataToWrite[i] >> 8) & 0xff);
                    i += 1;
                }
            }

            return WriteRawData(ParsedResult, numberOfElements * ParsedResult.BytesPerElements, ConvertedData);
        }

        //* Write a String
        /// <summary>
        /// Write a string value to a string data table
        /// The startAddress is in the common form of AB addressing (e.g. ST9:0)
        /// </summary>
        /// <param name="startAddress"></param>
        /// <param name="dataToWrite"></param>
        /// <returns></returns>
        /// <remarks></remarks>
        public int WriteData(string startAddress, string dataToWrite)
        {
            if (dataToWrite == null)
            {
                return 0;
            }

            ParsedDataAddress ParsedResult = ParseAddress(startAddress);

            //* Add an extra character to compensate for characters written in pairs to integers
            byte[] ConvertedData = new byte[dataToWrite.Length + 2 + 2];
            dataToWrite += Strings.Chr(0);

            ConvertedData[0] = dataToWrite.Length - 1;
            int i = 2;
            while (i <= dataToWrite.Length)
            {
                ConvertedData[i + 1] = Strings.Asc(dataToWrite.Substring(i - 2, 1));
                ConvertedData[i] = Strings.Asc(dataToWrite.Substring(i - 1, 1));
                i += 2;
            }
            //Array.Copy(System.Text.Encoding.Default.GetBytes(dataToWrite), 0, ConvertedData, 2, dataToWrite.Length)

            return WriteRawData(ParsedResult, dataToWrite.Length + 2, ConvertedData);
        }

        //**************************************************************
        //* Write to a PLC data file
        //*
        //**************************************************************
        private int WriteRawData(ParsedDataAddress ParsedResult, int numberOfBytes, byte[] dataToWrite)
        {
            //Dim dataC As New System.Collections.ObjectModel.Collection(Of Byte)

            //* Invalid address?
            if (ParsedResult.FileType == 0)
            {
                return -5;
            }

            //**********************************************
            //* Use a bit level function if it is bit level
            //**********************************************
            byte FunctionNumber = 0;

            int FilePosition = 0;
            int NumberOfBytesToWrite = 0;
            int DataStartPosition = 0;

            int reply = 0;
            int rTNS = 0;

            while (FilePosition < numberOfBytes && reply == 0)
            {
                //* Set next length of data to read. Max of 236 (slc 5/03 and up)
                //* This must limit to 82 for 5/02 and below
                if (numberOfBytes - FilePosition < 164)
                {
                    NumberOfBytesToWrite = numberOfBytes - FilePosition;
                }
                else
                {
                    NumberOfBytesToWrite = 164;
                }

                //* These files seem to be a special case
                if (ParsedResult.FileType >= 0xa1 & NumberOfBytesToWrite > 0x78)
                {
                    NumberOfBytesToWrite = 0x78;
                }

                int DataSize = NumberOfBytesToWrite + 4;

                //* For now we are only going to allow one bit to be set/reset per call
                if (ParsedResult.BitNumber < 16)
                    DataSize = 8;

                if (ParsedResult.Element >= 255)
                    DataSize += 2;
                if (ParsedResult.SubElement >= 255)
                    DataSize += 2;

                byte[] DataW = new byte[DataSize + 1];

                //* Byte Size
                DataW[0] = ((NumberOfBytesToWrite & 0xff));
                //* File Number
                DataW[1] = (ParsedResult.FileNumber);
                //* File Type
                DataW[2] = (ParsedResult.FileType);
                //* Starting Element Number
                if (ParsedResult.Element < 255)
                {
                    DataW[3] = (ParsedResult.Element);
                }
                else
                {
                    DataW[5] = Math.Floor(ParsedResult.Element / 256);
                    DataW[4] = ParsedResult.Element - (DataW[5] * 256);
                    //*  calculate offset
                    DataW[3] = 255;
                }

                //* Sub Element
                if (ParsedResult.SubElement < 255)
                {
                    DataW[DataW.Length - 1 - NumberOfBytesToWrite] = ParsedResult.SubElement;
                }
                else
                {
                    //* Use extended addressing
                    DataW[DataW.Length - 1 - NumberOfBytesToWrite] = Math.Floor(ParsedResult.SubElement / 256);
                    //* 256+data[5]
                    DataW[DataW.Length - 2 - NumberOfBytesToWrite] = ParsedResult.SubElement - (DataW[DataW.Length - 1 - NumberOfBytesToWrite] * 256);
                    //*  calculate offset
                    DataW[DataW.Length - 3 - NumberOfBytesToWrite] = 255;
                }

                //* Are we changing a single bit?
                if (ParsedResult.BitNumber < 16)
                {
                    FunctionNumber = 0xab;
                    //* Ref http://www.iatips.com/pccc_tips.html#slc5_cmds
                    //* Set the mask of which bit to change
                    DataW[DataW.Length - 4] = ((Math.Pow(2, (ParsedResult.BitNumber))) & 0xff);
                    DataW[DataW.Length - 3] = (Math.Pow(2, (ParsedResult.BitNumber - 8)));

                    if (dataToWrite[0] <= 0)
                    {
                        //* Set bits to clear 
                        DataW[DataW.Length - 2] = 0;
                        DataW[DataW.Length - 1] = 0;
                    }
                    else
                    {
                        //* Bits to turn on
                        DataW[DataW.Length - 2] = ((Math.Pow(2, (ParsedResult.BitNumber))) & 0xff);
                        DataW[DataW.Length - 1] = (Math.Pow(2, (ParsedResult.BitNumber - 8)));
                    }
                }
                else
                {
                    FunctionNumber = 0xaa;
                    DataStartPosition = DataW.Length - NumberOfBytesToWrite;

                    //* Prevent index out of range when numberToWrite exceeds dataToWrite.Length
                    int ValuesToMove = NumberOfBytesToWrite - 1;
                    if (ValuesToMove + FilePosition > dataToWrite.Length - 1)
                    {
                        ValuesToMove = dataToWrite.Length - 1 - FilePosition;
                    }

                    for (int i = 0; i <= ValuesToMove; i++)
                    {
                        DataW[i + DataStartPosition] = dataToWrite[i + FilePosition];
                    }
                }

                reply = PrefixAndSend(0xf, FunctionNumber, DataW, !m_AsyncMode, ref rTNS);

                FilePosition += NumberOfBytesToWrite;

                if (ParsedResult.FileType != 0xa4)
                {
                    //* Use subelement because it works with all data file types
                    ParsedResult.SubElement += NumberOfBytesToWrite / 2;
                }
                else
                {
                    //* Special case file - 28h bytes per elements
                    ParsedResult.Element += NumberOfBytesToWrite / 0x28;
                }
            }

            if (reply == 0)
            {
                return 0;
            }
            else
            {
                throw new DF1Exception(DecodeMessage(reply));
            }
        }
        //End of Public Methods
        #endregion

        #region "Shared Methods"
        //****************************************************************
        //* Convert an array of words into a string as AB PLC's represent
        //* Can be used when reading a string from an Integer file
        //****************************************************************
        /// <summary>
        /// Convert an array of integers to a string
        /// This is used when storing strings in an integer data table
        /// </summary>
        /// <param name="words"></param>
        /// <returns></returns>
        /// <remarks></remarks>
        public static string WordsToString(Int32[] words)
        {
            int WordCount = words.Length;
            return WordsToString(words, 0, WordCount);
        }

        /// <summary>
        /// Convert an array of integers to a string
        /// This is used when storing strings in an integer data table
        /// </summary>
        /// <param name="words"></param>
        /// <param name="index"></param>
        /// <returns></returns>
        /// <remarks></remarks>
        public static string WordsToString(Int32[] words, int index)
        {
            int WordCount = (words.Length - index);
            return WordsToString(words, index, WordCount);
        }

        /// <summary>
        /// Convert an array of integers to a string
        /// This is used when storing strings in an integer data table
        /// </summary>
        /// <param name="words"></param>
        /// <param name="index"></param>
        /// <param name="wordCount"></param>
        /// <returns></returns>
        /// <remarks></remarks>
        public static string WordsToString(Int32[] words, int index, int wordCount)
        {
            int j = index;
            System.Text.StringBuilder result2 = new System.Text.StringBuilder();
            while (j < wordCount)
            {
                result2.Append(Strings.Chr(words[j] / 256));
                //* Prevent an odd length string from getting a Null added on
                if (Convert.ToInt32(words[j] & 0xff) > 0)
                {
                    result2.Append(Strings.Chr(words[j] & 0xff));
                }
                j += 1;
            }

            return result2.ToString();
        }


        //**********************************************************
        //* Convert a string to an array of words
        //*  Can be used when writing a string to an Integer file
        //**********************************************************
        /// <summary>
        /// Convert a string to an array of words
        /// Can be used when writing a string into an integer data table
        /// </summary>
        /// <param name="source"></param>
        /// <returns></returns>
        /// <remarks></remarks>
        public static Int32[] StringToWords(string source)
        {
            if (source == null)
            {
                return null;
                // Throw New ArgumentNullException("input")
            }

            int ArraySize = Convert.ToInt32(Math.Ceiling(source.Length / 2)) - 1;

            Int32[] ConvertedData = new Int32[ArraySize + 1];

            int i = 0;
            while (i <= ArraySize)
            {
                ConvertedData[i] = Strings.Asc(source.Substring(i * 2, 1)) * 256;
                //* Check if past last character of odd length string
                if ((i * 2) + 1 < source.Length)
                    ConvertedData[i] += Strings.Asc(source.Substring((i * 2) + 1, 1));
                i += 1;
            }

            return ConvertedData;
        }

        #endregion

        #region "Helper"
        private struct ParsedDataAddress
        {
            public int FileType;
            public int FileNumber;
            public int Element;
            public int SubElement;
            public int BitNumber;
            public int BytesPerElements;
            public int TableSizeInBytes;
        }

        //*********************************************************************************
        //* Parse the address string and validate, if invalid, Return 0 in FileType
        //* Convert the file type letter Type to the corresponding value
        //* Reference page 7-18
        //*********************************************************************************
        private Regex RE1 = new Regex("(?i)^\\s*(?<FileType>([SBCTRNFAIOL])|(ST)|(MG)|(PD)|(PLS))(?<FileNumber>\\d{1,3}):(?<ElementNumber>\\d{1,3})(/(?<BitNumber>\\d{1,4}))?\\s*$");
        private Regex RE2 = new Regex("(?i)^\\s*(?<FileType>[BN])(?<FileNumber>\\d{1,3})(/(?<BitNumber>\\d{1,4}))\\s*$");
        private Regex RE3 = new Regex("(?i)^\\s*(?<FileType>[CT])(?<FileNumber>\\d{1,3}):(?<ElementNumber>\\d{1,3})[.](?<SubElement>(ACC|PRE|EN|DN|TT|CU|CD|DN|OV|UN|UA))\\s*$");
        //* IO variation without file number Type (Input : file 1, Output : file 0 )
        private Regex RE4 = new Regex("(?i)^\\s*(?<FileType>([IOS])):(?<ElementNumber>\\d{1,3})([.](?<SubElement>[0-7]))?(/(?<BitNumber>\\d{1,4}))?\\s*$");
        private ParsedDataAddress ParseAddress(string DataAddress)
        {
            ParsedDataAddress result = default(ParsedDataAddress);

            result.FileType = 0;
            //* Let a 0 inidcated an invalid address
            result.BitNumber = 99;
            //* Let a 99 indicate no bit level requested

            //*********************************
            //* Try all match patterns
            //*********************************
            MatchCollection mc = RE1.Matches(DataAddress);

            if (mc.Count <= 0)
            {
                mc = RE2.Matches(DataAddress);
                if (mc.Count <= 0)
                {
                    mc = RE3.Matches(DataAddress);
                    if (mc.Count <= 0)
                    {
                        mc = RE4.Matches(DataAddress);
                        if (mc.Count <= 0)
                        {
                            return result;
                        }
                    }
                }
            }

            //*********************************************
            //* Get elements extracted from match patterns
            //*********************************************
            //* Is it an I,O, or S address without a file number Type?
            if (mc[0].Groups["FileNumber"].Length == 0)
            {
                // Is it an input or Output file?
                if (DataAddress.IndexOf("i") >= 0 | DataAddress.IndexOf("I") >= 0)
                {
                    result.FileNumber = 1;
                }
                else if (DataAddress.IndexOf("o") >= 0 | DataAddress.IndexOf("O") >= 0)
                {
                    result.FileNumber = 0;
                }
                else
                {
                    result.FileNumber = 2;
                }
            }
            else
            {
                result.FileNumber = mc[0].Groups["FileNumber"].ToString();
            }


            if (mc[0].Groups["BitNumber"].Length > 0)
            {
                result.BitNumber = mc[0].Groups["BitNumber"].ToString();
            }

            if (mc[0].Groups["ElementNumber"].Length > 0)
            {
                result.Element = mc[0].Groups["ElementNumber"].ToString();
            }
            else
            {
                result.Element = result.BitNumber >> 4;
                result.BitNumber = result.BitNumber % 16;
            }

            if (mc[0].Groups["SubElement"].Length > 0)
            {
                switch (mc[0].Groups["SubElement"].ToString().ToUpper(System.Globalization.CultureInfo.CurrentCulture))
                {
                    case "PRE":
                        result.SubElement = 1;
                        break;
                    case "ACC":
                        result.SubElement = 2;
                        break;
                    case "EN":
                        result.SubElement = 15;
                        break;
                    case "TT":
                        result.SubElement = 14;
                        break;
                    case "DN":
                        result.SubElement = 13;
                        break;
                    case "CU":
                        result.SubElement = 15;
                        break;
                    case "CD":
                        result.SubElement = 14;
                        break;
                    case "OV":
                        result.SubElement = 12;
                        break;
                    case "UN":
                        result.SubElement = 11;
                        break;
                    case "UA":
                        result.SubElement = 10;
                        break;
                    case "0":
                        result.SubElement = 0;
                        break;
                    case "1":
                        result.SubElement = 1;
                        break;
                    case "2":
                        result.SubElement = 2;
                        break;
                    case "3":
                        result.SubElement = 3;
                        break;
                    case "4":
                        result.SubElement = 4;
                        break;
                    case "5":
                        result.SubElement = 5;
                        break;
                    case "6":
                        result.SubElement = 6;
                        break;
                    case "7":
                        result.SubElement = 7;
                        break;
                    case "8":
                        result.SubElement = 8;
                        break;
                }
            }


            //* These subelements are bit level
            if (result.SubElement > 4)
            {
                result.SubElement = 0;
                result.BitNumber = result.SubElement;
            }


            //***************************************
            //* Translate file type letter to number
            //***************************************
            if (result.Element < 256)
            {
                result.BytesPerElements = 2;

                string FileType = mc[0].Groups["FileType"].ToString().ToUpper(System.Globalization.CultureInfo.CurrentCulture);
                switch (FileType)
                {
                    case "N":
                        result.FileType = 0x89;
                        break;
                    case "B":
                        result.FileType = 0x85;
                        break;
                    case "T":
                        result.FileType = 0x86;
                        break;
                    case "C":
                        result.FileType = 0x87;
                        break;
                    case "F":
                        result.FileType = 0x8a;
                        result.BytesPerElements = 4;
                        break;
                    case "S":
                        result.FileType = 0x84;
                        break;
                    case "ST":
                        result.FileType = 0x8d;
                        result.BytesPerElements = 76;
                        break;
                    case "A":
                        result.FileType = 0x8e;
                        break;
                    case "R":
                        result.FileType = 0x88;
                        break;
                    case "O":
                        result.FileType = 0x8b;
                        break;
                    case "I":
                        result.FileType = 0x8c;
                        break;
                    case "L":
                        result.FileType = 0x91;
                        result.BytesPerElements = 4;
                        break;
                    case "MG":
                        result.FileType = 0x92;
                        //Message Command 146
                        result.BytesPerElements = 50;
                        break;
                    case "PD":
                        result.FileType = 0x93;
                        //PID
                        result.BytesPerElements = 46;
                        break;
                    case "PLS":
                        result.FileType = 0x94;
                        //Programmable Limit Swith
                        result.BytesPerElements = 12;
                        break;
                }
            }

            return result;
        }

        //****************************************************
        //* Wait for a response from PLC before returning
        //****************************************************
        //* 50 ticks per second
        int MaxTicks = 100;
        private int WaitForResponse(int rTNS)
        {
            //Responded = False

            int Loops = 0;
            while (!Responded(rTNS) & Loops < MaxTicks)
            {
                //Application.DoEvents()
                System.Threading.Thread.Sleep(20);
                Loops += 1;
            }

            if (Loops >= MaxTicks)
            {
                return -20;
            }
            else if (LastResponseWasNAK)
            {
                return -21;
            }
            else
            {
                return 0;
            }
        }

        //**************************************************************
        //* This method implements the common application routine
        //* as discussed in the Software Layer section of the AB manual
        //**************************************************************
        private int PrefixAndSend(byte Command, byte Func, byte[] data, bool Wait, ref int rTNS)
        {
            //Dim CommandPacket As New System.Collections.ObjectModel.Collection(Of Byte)

            IncrementTNS();


            int PacketSize = 0;
            if (m_Protocol == "DF1")
            {
                PacketSize = data.Length + 6;
            }
            else
            {
                PacketSize = data.Length + 10;
            }


            byte[] CommandPacke = new byte[PacketSize + 1];
            int BytePos = 0;

            if (m_Protocol == "DF1")
            {
                CommandPacke[0] = m_TargetNode;
                CommandPacke[1] = m_MyNode;
                BytePos = 2;
            }
            else
            {
                CommandPacke[0] = m_TargetNode + 0x80;
                CommandPacke[1] = 0x88;
                //* Not sure what this is, must be command code for DF1 packet. Sometimes it's &H18
                CommandPacke[2] = m_MyNode + 0x80;
                CommandPacke[3] = 1;
                CommandPacke[4] = 1;
                CommandPacke[5] = data.Length + 5;
                //*Length of DF1 data packet
                BytePos = 6;
            }

            CommandPacke[BytePos] = Command;
            CommandPacke[BytePos + 1] = 0;
            //* STS (status, always 0)

            CommandPacke[BytePos + 2] = (TNS & 255);
            CommandPacke[BytePos + 3] = (TNS >> 8);

            CommandPacke[BytePos + 4] = Func;

            data.CopyTo(CommandPacke, BytePos + 5);

            rTNS = TNS & 0xff;
            Responded[rTNS] = false;
            int result = 0;
            if (m_Protocol == "DF1")
            {
                ACKed[TNS & 255] = false;
                result = SendData(CommandPacke);
            }
            else
            {
                if (!SerialPort.IsOpen)
                    OpenComms();
                QueuedCommand.Clear();
                for (int j = 0; j <= CommandPacke.Length - 1; j++)
                {
                    QueuedCommand.Add(CommandPacke[j]);
                }
                //Array.Copy(CommandPacke, QueuedCommand, CommandPacke.Length)
                //QueuedCommandSize = CommandPacke.Length
                CommandInQueue = true;
            }


            if (result == 0 & Wait)
            {
                result = WaitForResponse(rTNS);

                //* Return status byte that came from controller
                if (result == 0)
                {
                    if (DataPackets[rTNS] != null)
                    {
                        if (m_Protocol == "DF1")
                        {
                            if ((DataPackets[rTNS].Count > 3))
                            {
                                result = DataPackets[rTNS][3];
                                //* STS position in DF1 message
                                //* If its and EXT STS, page 8-4
                                if (result == 0xf0)
                                {
                                    //* The EXT STS is the last byte in the packet
                                    //result = DataPackets(rTNS)(DataPackets(rTNS).Count - 2) + &H100
                                    result = DataPackets[rTNS][DataPackets[rTNS].Count - 1] + 0x100;
                                }
                            }
                        }
                        else
                        {
                            //* STS position in DH485 message
                            if (DataPackets[rTNS].Count > 7)
                            {
                                result = DataPackets[rTNS][7];
                            }
                        }
                    }
                    else
                    {
                        result = -8;
                        //* no response came back from PLC
                    }
                }
                else
                {
                    int DebugCheck = 0;
                }
            }
            else
            {
                int DebugCheck = 0;
            }

            return result;
        }

        //**************************************************************
        //* This method Sends a response from an unsolicited msg
        //**************************************************************
        private int SendResponse(byte Command, int rTNS)
        {
            int PacketSize = 0;
            //PacketSize = Data.Length + 5
            PacketSize = 5;


            byte[] CommandPacke = new byte[PacketSize + 1];
            int BytePos = 0;

            CommandPacke[1] = m_TargetNode;
            CommandPacke[0] = m_MyNode;
            BytePos = 2;

            CommandPacke[BytePos] = Command;
            CommandPacke[BytePos + 1] = 0;
            //* STS (status, always 0)

            CommandPacke[BytePos + 2] = (rTNS & 255);
            CommandPacke[BytePos + 3] = (rTNS >> 8);


            int result = 0;
            result = SendData(CommandPacke);
            return result;
        }

        private void IncrementTNS()
        {
            //* Incement the TransactionNumber value
            if (TNS < 65535)
            {
                TNS += 1;
            }
            else
            {
                TNS = 1;
            }
        }

        //************************************************
        //* Conver the message code number into a string
        //* Ref Page 8-3'************************************************
        public static string DecodeMessage(int msgNumber)
        {
            string functionReturnValue = null;
            switch (msgNumber)
            {
                case 0:
                    functionReturnValue = "";
                    break;
                case -2:
                    return "Not Acknowledged (NAK)";
                case -3:
                    return "No Reponse, Check COM Settings";
                case -4:
                    return "Unknown Message from DataLink Layer";
                case -5:
                    return "Invalid Address";
                case -6:
                    return "Could Not Open Com Port";
                case -7:
                    return "No data specified to data link layer";
                case -8:
                    return "No data returned from PLC";
                case -20:
                    return "No Data Returned";
                case -21:

                    return "Received Message NAKd from invalid checksum";
                //*** Errors coming from PLC
                case 16:
                    return "Illegal Command or Format, Address may not exist or not enough elements in data file";
                case 32:
                    return "PLC Has a Problem and Will Not Communicate";
                case 48:
                    return "Remote Node Host is Misssing, Disconnected, or Shut Down";
                case 64:
                    return "Host Could Not Complete Function Due To Hardware Fault";
                case 80:
                    return "Addressing problem or Memory Protect Rungs";
                case 96:
                    return "Function not allows due to command protection selection";
                case 112:
                    return "Processor is in Program mode";
                case 128:
                    return "Compatibility mode file missing or communication zone problem";
                case 144:
                    return "Remote node cannot buffer command";
                case 240:

                    return "Error code in EXT STS Byte";
                //* EXT STS Section - 256 is added to code to distinguish EXT codes
                case 257:
                    return "A field has an illegal value";
                case 258:
                    return "Less levels specified in address than minimum for any address";
                case 259:
                    return "More levels specified in address than system supports";
                case 260:
                    return "Symbol not found";
                case 261:
                    return "Symbol is of improper format";
                case 262:
                    return "Address doesn't point to something usable";
                case 263:
                    return "File is wrong size";
                case 264:
                    return "Cannot complete request, situation has changed since the start of the command";
                case 265:
                    return "Data or file is too large";
                case 266:
                    return "Transaction size plus word address is too large";
                case 267:
                    return "Access denied, improper priviledge";
                case 268:
                    return "Condition cannot be generated - resource is not available";
                case 269:
                    return "Condition already exists - resource is already available";
                case 270:

                    return "Command cannot be executed";
                default:
                    return "Unknown Message - " + msgNumber;
            }
            return functionReturnValue;
        }


        //Private Sub DF1DataLink1_DataReceived(ByVal sender As Object, ByVal e As System.EventArgs) Handles Me.DataReceivedDL
        private void DF1DataLink1_DataReceived()
        {
            // DataString(LastDataIndex) = DataStrings(LastDataIndex)

            //* Should we only raise an event if we are in AsyncMode?
            //        If m_AsyncMode Then
            //**************************************************************************
            //* If the parent form property is set, then sync the event with its thread
            //**************************************************************************
            //* This was moved
            //Responded(m_LastDataIndex) = True

            //RaiseEvent DataReceived(Me, System.EventArgs.Empty)
            //Exit Sub

            if (!DisableEvent)
            {
                
                    if (DataReceived != null)
                    {
                        DataReceived(this, System.EventArgs.Empty);
                    }
            }
            //End If
        }

        //******************************************************************
        //* This is called when a message instruction was sent from the PLC
        //******************************************************************
        private void DF1DataLink1_UnsolictedMessageRcvd()
        {
            
                if (UnsolictedMessageRcvd != null)
                {
                    UnsolictedMessageRcvd(this, System.EventArgs.Empty);
                }
        }


        //****************************************************************************
        //* This is required to sync the event back to the parent form's main thread
        //****************************************************************************
        //Delegate Sub DataReceivedSyncDel(ByVal sender As Object, ByVal e As EventArgs)
        private void DataReceivedSync(object sender, EventArgs e)
        {
            if (DataReceived != null)
            {
                DataReceived(sender, e);
            }
        }
        private void UnsolictedMessageRcvdSync(object sender, EventArgs e)
        {
            if (UnsolictedMessageRcvd != null)
            {
                UnsolictedMessageRcvd(sender, e);
            }
        }
        #endregion

        #region "Data Link Layer"
        //**************************************************************************************************************
        //**************************************************************************************************************
        //**************************************************************************************************************
        //*****                                  DATA LINK LAYER SECTION
        //**************************************************************************************************************
        //**************************************************************************************************************

        //Private Response As ResponseTypes
        private System.Collections.ObjectModel.Collection<byte>[] DataPackets = new System.Collections.ObjectModel.Collection<byte>[256];

        private bool LastResponseWasNAK;
        private System.IO.Ports.SerialPort withEventsField_SerialPort = new System.IO.Ports.SerialPort();
        private System.IO.Ports.SerialPort SerialPort
        {
            get { return withEventsField_SerialPort; }
            set
            {
                if (withEventsField_SerialPort != null)
                {
                    withEventsField_SerialPort.DataReceived -= SerialPort_DataReceived;
                    withEventsField_SerialPort.ErrorReceived -= SerialPort_ErrorReceived;
                }
                withEventsField_SerialPort = value;
                if (withEventsField_SerialPort != null)
                {
                    withEventsField_SerialPort.DataReceived += SerialPort_DataReceived;
                    withEventsField_SerialPort.ErrorReceived += SerialPort_ErrorReceived;
                }
            }

        }
        //Private Enum ResponseTypes
        //    NoResponse
        //    AXcknowledged
        //    NotAXcknowledged
        //    TimeOut
        //    DataReturned
        //    Enquire
        //End Enum

        //Private Event DataReceivedDL(ByVal sender As Object, ByVal e As EventArgs)


        //*********************************************************
        //* This keeps a buffer of the last 256 messages received
        //* Its key is based on the LSB of the TNS value
        //*********************************************************
        //Private m_LastDataIndex As Integer
        //Public ReadOnly Property LastDataIndex() As Integer
        //    Get
        //        Return m_LastDataIndex
        //    End Get
        //End Property

        //*******************************
        //* Table for calculating CRC
        //*******************************
        private UInt16[] aCRC16Table = {
        0x0,
        0xc0c1,
        0xc181,
        0x140,
        0xc301,
        0x3c0,
        0x280,
        0xc241,
        0xc601,
        0x6c0,
        0x780,
        0xc741,
        0x500,
        0xc5c1,
        0xc481,
        0x440,
        0xcc01,
        0xcc0,
        0xd80,
        0xcd41,
        0xf00,
        0xcfc1,
        0xce81,
        0xe40,
        0xa00,
        0xcac1,
        0xcb81,
        0xb40,
        0xc901,
        0x9c0,
        0x880,
        0xc841,
        0xd801,
        0x18c0,
        0x1980,
        0xd941,
        0x1b00,
        0xdbc1,
        0xda81,
        0x1a40,
        0x1e00,
        0xdec1,
        0xdf81,
        0x1f40,
        0xdd01,
        0x1dc0,
        0x1c80,
        0xdc41,
        0x1400,
        0xd4c1,
        0xd581,
        0x1540,
        0xd701,
        0x17c0,
        0x1680,
        0xd641,
        0xd201,
        0x12c0,
        0x1380,
        0xd341,
        0x1100,
        0xd1c1,
        0xd081,
        0x1040,
        0xf001,
        0x30c0,
        0x3180,
        0xf141,
        0x3300,
        0xf3c1,
        0xf281,
        0x3240,
        0x3600,
        0xf6c1,
        0xf781,
        0x3740,
        0xf501,
        0x35c0,
        0x3480,
        0xf441,
        0x3c00,
        0xfcc1,
        0xfd81,
        0x3d40,
        0xff01,
        0x3fc0,
        0x3e80,
        0xfe41,
        0xfa01,
        0x3ac0,
        0x3b80,
        0xfb41,
        0x3900,
        0xf9c1,
        0xf881,
        0x3840,
        0x2800,
        0xe8c1,
        0xe981,
        0x2940,
        0xeb01,
        0x2bc0,
        0x2a80,
        0xea41,
        0xee01,
        0x2ec0,
        0x2f80,
        0xef41,
        0x2d00,
        0xedc1,
        0xec81,
        0x2c40,
        0xe401,
        0x24c0,
        0x2580,
        0xe541,
        0x2700,
        0xe7c1,
        0xe681,
        0x2640,
        0x2200,
        0xe2c1,
        0xe381,
        0x2340,
        0xe101,
        0x21c0,
        0x2080,
        0xe041,
        0xa001,
        0x60c0,
        0x6180,
        0xa141,
        0x6300,
        0xa3c1,
        0xa281,
        0x6240,
        0x6600,
        0xa6c1,
        0xa781,
        0x6740,
        0xa501,
        0x65c0,
        0x6480,
        0xa441,
        0x6c00,
        0xacc1,
        0xad81,
        0x6d40,
        0xaf01,
        0x6fc0,
        0x6e80,
        0xae41,
        0xaa01,
        0x6ac0,
        0x6b80,
        0xab41,
        0x6900,
        0xa9c1,
        0xa881,
        0x6840,
        0x7800,
        0xb8c1,
        0xb981,
        0x7940,
        0xbb01,
        0x7bc0,
        0x7a80,
        0xba41,
        0xbe01,
        0x7ec0,
        0x7f80,
        0xbf41,
        0x7d00,
        0xbdc1,
        0xbc81,
        0x7c40,
        0xb401,
        0x74c0,
        0x7580,
        0xb541,
        0x7700,
        0xb7c1,
        0xb681,
        0x7640,
        0x7200,
        0xb2c1,
        0xb381,
        0x7340,
        0xb101,
        0x71c0,
        0x7080,
        0xb041,
        0x5000,
        0x90c1,
        0x9181,
        0x5140,
        0x9301,
        0x53c0,
        0x5280,
        0x9241,
        0x9601,
        0x56c0,
        0x5780,
        0x9741,
        0x5500,
        0x95c1,
        0x9481,
        0x5440,
        0x9c01,
        0x5cc0,
        0x5d80,
        0x9d41,
        0x5f00,
        0x9fc1,
        0x9e81,
        0x5e40,
        0x5a00,
        0x9ac1,
        0x9b81,
        0x5b40,
        0x9901,
        0x59c0,
        0x5880,
        0x9841,
        0x8801,
        0x48c0,
        0x4980,
        0x8941,
        0x4b00,
        0x8bc1,
        0x8a81,
        0x4a40,
        0x4e00,
        0x8ec1,
        0x8f81,
        0x4f40,
        0x8d01,
        0x4dc0,
        0x4c80,
        0x8c41,
        0x4400,
        0x84c1,
        0x8581,
        0x4540,
        0x8701,
        0x47c0,
        0x4680,
        0x8641,
        0x8201,
        0x42c0,
        0x4380,
        0x8341,
        0x4100,
        0x81c1,
        0x8081,
        0x4040

    };
        //*********************************
        //* This CRC uses a table lookup
        //* algorithm for faster computing
        //*********************************
        private int CalculateCRC16(byte[] DataInput)
        {
            UInt16 iCRC = 0;
            byte bytT = 0;


            for (int i = 0; i <= DataInput.Length - 1; i++)
            {
                bytT = (iCRC & 0xff) ^ DataInput[i];
                iCRC = (iCRC >> 8) ^ aCRC16Table[bytT];
            }

            //*** must do one more with ETX char
            bytT = (iCRC & 0xff) ^ 3;
            iCRC = (iCRC >> 8) ^ aCRC16Table[bytT];

            return iCRC;
        }

        //* Overload - Calc CRC on a collection of bytes
        private int CalculateCRC16(System.Collections.ObjectModel.Collection<byte> DataInput)
        {
            UInt16 iCRC = 0;
            byte bytT = 0;


            for (int i = 0; i <= DataInput.Count - 1; i++)
            {
                bytT = (iCRC & 0xff) ^ DataInput[i];
                iCRC = (iCRC >> 8) ^ aCRC16Table[bytT];
            }

            //*** must do one more with ETX char
            bytT = (iCRC & 0xff) ^ 3;
            iCRC = (iCRC >> 8) ^ aCRC16Table[bytT];

            return iCRC;
        }

        //***************************
        //* Calculate a BCC
        //***************************
        private static byte CalculateBCC(byte[] DataInput)
        {
            byte functionReturnValue = 0;
            int sum = 0;
            for (int i = 0; i <= DataInput.Length - 1; i++)
            {
                sum += DataInput[i];
            }

            functionReturnValue = sum & 0xff;
            functionReturnValue = (0x100 - functionReturnValue) & 255;
            return functionReturnValue;
            //* had to add the "and 255" for the case of sum being 0
        }
        //* Overload
        private static byte CalculateBCC(System.Collections.ObjectModel.Collection<byte> DataInput)
        {
            byte functionReturnValue = 0;
            int sum = 0;
            for (int i = 0; i <= DataInput.Count - 1; i++)
            {
                sum += DataInput[i];
            }

            functionReturnValue = sum & 0xff;
            functionReturnValue = (0x100 - functionReturnValue) & 255;
            return functionReturnValue;
            //* had to add the "and 255" for the case of sum being 0
        }

        //******************************************
        //* Handle Data Received On The Serial Port
        //******************************************
        private byte LastByte;
        private bool PacketStarted;
        private bool PacketEnded;
        private bool NodeChecked;
        byte b;
        private int ETXPosition;
        private System.Collections.ObjectModel.Collection<byte> ReceivedDataPacket = new System.Collections.ObjectModel.Collection<byte>();
        private void SerialPort_DataReceived(object sender, System.IO.Ports.SerialDataReceivedEventArgs e)
        {
            int BytesToRead = SerialPort.BytesToRead;

            byte[] BytesRead = new byte[BytesToRead];
            SerialPort.Read(BytesRead, 0, BytesToRead);


            int i = 0;

            while (i < BytesToRead)
            {
                b = BytesRead[i];

                //**************************************************************
                //* Do not start capturing chars until start of packet received
                //**************************************************************
                if (PacketStarted)
                {
                    //* filter out double 16's
                    if (LastByte == 16 & b == 16)
                    {
                        b = 0;
                        LastByte = 0;
                    }
                    else
                    {
                        ReceivedDataPacket.Add(b);
                    }

                    //* Is there another start sequence?
                    if (LastByte == 16 & b == 2)
                    {
                        ReceivedDataPacket.Clear();
                    }

                    //* Ignore data if not addressed to this node
                    if (!NodeChecked)
                    {
                        if (m_Protocol != "DF1" & b != m_MyNode + 0x80)
                        {
                            PacketStarted = false;
                            PacketEnded = false;
                            ReceivedDataPacket.Clear();
                        }
                        else
                        {
                            NodeChecked = true;
                            SerialPort.DtrEnable = false;
                        }
                    }
                }


                //******************
                //* DLE character
                //******************
                if (LastByte == 16)
                {
                    //******************
                    //* STX Sequence
                    //******************
                    if (b == 2)
                    {
                        PacketStarted = true;
                        NodeChecked = false;
                    }

                    //*******************
                    //* ETX Sequence
                    //*******************
                    if (PacketStarted && b == 3)
                    {
                        PacketEnded = true;
                        ETXPosition = ReceivedDataPacket.Count - 2;
                    }

                    //********************************
                    //* Handle DF1 Control Characters
                    //********************************
                    if (m_Protocol == "DF1" && b != 2 && b != 16 && b != 3)
                    {
                        //***************
                        //* ACK sequence
                        //***************
                        if (b == 6)
                        {
                            System.Threading.Thread.Sleep(SleepDelay);
                            Acknowledged = true;
                            ACKed[TNS & 255] = true;
                        }

                        //***************
                        //* NAK Sequence
                        //***************
                        if (b == 21)
                        {
                            NotAcknowledged = true;
                        }

                        //***************
                        //* ENQ Sequence
                        //***************
                        if (b == 5)
                        {
                            //* We can handle the ENQ right here
                            //Response = ResponseTypes.Enquire

                            //* Reply with last response back to PLC
                            byte[] ACKSequence = {
                            16,
                            6
                        };
                            if (LastResponseWasNAK)
                            {
                                ACKSequence[1] = 0x15;
                                //* NAK
                            }
                            SerialPort.Write(ACKSequence, 0, 2);
                        }

                        //* Removed this because it cleared out good data when a "16" byte came
                        //ReceivedDataPacket.Clear()
                    }
                }



                if (PacketEnded)
                {
                    if ((m_CheckSum == CheckSumOptions.Bcc & (ReceivedDataPacket.Count - ETXPosition) >= 3) | (m_CheckSum == CheckSumOptions.Crc & (ReceivedDataPacket.Count - ETXPosition) >= 4))
                    {
                        ProcessReceivedData();
                        PacketStarted = false;
                        PacketEnded = false;
                        b = 0;
                        //make sure last byte isn't falsely 16
                        ReceivedDataPacket.Clear();
                    }
                }


                LastByte = b;
                i += 1;
            }
        }

        //*********************************
        //* Created for auto configure
        //*********************************
        private int SendENQ()
        {
            if (!SerialPort.IsOpen)
            {
                int OpenResult = OpenComms();
                if (OpenResult != 0)
                    return OpenResult;
            }

            byte[] ENQSequence = {
            16,
            5
        };
            Acknowledged = false;
            NotAcknowledged = false;
            SerialPort.Write(ENQSequence, 0, 2);

            AckWaitTicks = 0;
            while ((!Acknowledged & !NotAcknowledged) & AckWaitTicks < MaxTicks)
            {
                System.Threading.Thread.Sleep(20);
                AckWaitTicks += 1;
            }

            if (AckWaitTicks >= MaxTicks)
                return -3;

            return 0;
        }


        private bool PacketOpened;
        //*******************************************************************
        //* When a complete packet is received from the PLC, this is called
        //*******************************************************************
        private void ProcessReceivedData()
        {
            //* Get the Checksum that came back from the PLC
            UInt16 CheckSumResult = default(UInt16);
            if (m_CheckSum == CheckSumOptions.Bcc)
            {
                CheckSumResult = ReceivedDataPacket(ETXPosition + 2);
            }
            else
            {
                CheckSumResult = (ReceivedDataPacket(ETXPosition + 2)) + (ReceivedDataPacket(ETXPosition + 3)) * 256;
            }


            //****************************
            //* validate CRC received
            //****************************
            //* Store the returned data in an array based on the LSB of the TNS
            //* If there is no TNS, then store in 0
            int xTNS = 0;

            //* make sure there is enough data and it is not a command (commands are less than 31)
            if (ETXPosition > 4 & ReceivedDataPacket(2) > 31)
            {
                xTNS = ReceivedDataPacket(4);
            }
            else
            {
                xTNS = 0;
            }

            if (m_Protocol != "DF1" && ETXPosition > 8)
            {
                xTNS = ReceivedDataPacket[8];
            }

            //********************************************************************
            //* Store data in a array of collection using TNS's low byte as index
            //********************************************************************
            if (DataPackets(xTNS) != null)
            {
                DataPackets(xTNS).Clear();
            }
            else
            {
                DataPackets(xTNS) = new System.Collections.ObjectModel.Collection<byte>();
            }

            for (int i = 0; i <= ETXPosition - 1; i++)
            {
                DataPackets(xTNS).Add(ReceivedDataPacket(i));
            }


            //* Calculate the checksum for the received data
            int CheckSumCalc = 0;
            if (m_CheckSum == CheckSumOptions.Bcc)
            {
                CheckSumCalc = CalculateBCC(DataPackets(xTNS));
            }
            else
            {
                CheckSumCalc = CalculateCRC16(DataPackets(xTNS));
            }


            //***************************************************************************
            //* Send back an response to indicate whether data was received successfully
            //***************************************************************************
            byte[] ACKSequence = {
            16,
            6
        };
            //CheckSumCalc = 0 '*** DEBUG - fail checksum
            if (CheckSumResult == CheckSumCalc)
            {
                if (m_Protocol == "DF1")
                {
                    Responded(xTNS) = true;

                    if (DataPackets(xTNS)(2) > 31)
                    {
                        //* Let application layer know that new data has came back
                        DF1DataLink1_DataReceived();
                    }
                    else
                    {
                        //****************************************************
                        //****************************************************
                        //* Handle the unsolicited message
                        //* This is where the simulator code would be placed
                        //****************************************************
                        //* Command &h0F Function &HAA - Logical Write
                        if (DataPackets(xTNS)(2) == 15 & DataPackets(xTNS)(6) == 0xaa)
                        {
                            //* Send back response - Page 7-18
                            int TNS = 0;
                            TNS = DataPackets(xTNS)(5) * 256 + DataPackets(xTNS)(4);
                            SendResponse(DataPackets(xTNS)(2) + 0x40, TNS);

                            //* Extract the information
                            int ElementCount = DataPackets(xTNS)(7);
                            int FileNumber = DataPackets(xTNS)(8);
                            int FileType = DataPackets(xTNS)(9);
                            int Element = DataPackets(xTNS)(10);
                            int SubElement = DataPackets(xTNS)(11);
                            string StringFileType = null;
                            int BytesPerElement = 0;

                            switch (FileType)
                            {
                                case 0x89:
                                    StringFileType = "N";
                                    BytesPerElement = 2;
                                    break;
                                case 0x85:
                                    StringFileType = "B";
                                    BytesPerElement = 2;
                                    break;
                                case 0x86:
                                    StringFileType = "T";
                                    BytesPerElement = 6;
                                    break;
                                case 0x87:
                                    StringFileType = "C";
                                    BytesPerElement = 6;
                                    break;
                                case 0x84:
                                    StringFileType = "S";
                                    BytesPerElement = 2;
                                    break;
                                case 0x8a:
                                    StringFileType = "F";
                                    BytesPerElement = 4;
                                    break;
                                case 0x8d:
                                    StringFileType = "ST";
                                    BytesPerElement = 84;
                                    break;
                                case 0x8e:
                                    StringFileType = "A";
                                    BytesPerElement = 2;
                                    break;
                                case 0x88:
                                    StringFileType = "R";
                                    BytesPerElement = 6;
                                    break;
                                case 0x82:
                                case 0x8b:
                                    StringFileType = "O";
                                    BytesPerElement = 2;
                                    break;
                                case 0x83:
                                case 0x8c:
                                    StringFileType = "I";
                                    BytesPerElement = 2;

                                    break;
                                default:
                                    StringFileType = "Undefined";
                                    BytesPerElement = 2;
                                    break;
                            }


                            //* Raise the event to let know that a command was rcvd
                            DF1DataLink1_UnsolictedMessageRcvd();
                        }
                        return;
                    }
                }

                //* Keep this in case the PLC requests with ENQ
                LastResponseWasNAK = false;
            }
            else
            {
                ACKSequence[1] = 0x15;
                //* NAK
                AckWaitTicks = 0;

                //* Keep this in case the PLC requests with ENQ
                Responded[xTNS] = true;
                LastResponseWasNAK = true;

                //* Slow down comms - helps with USB converter
                if (SleepDelay < 400)
                    SleepDelay += 50;
            }

            //*********************************
            //* Respond according to protocol
            if (m_Protocol == "DF1")
            {
                //* Send the ACK or NAK back to the PLC
                SerialPort.Write(ACKSequence, 0, 2);
            }
            else
            {
                //*********************************************
                //* DH485 command responses
                //**********************************************
                //* Ack command received from DH485
                if (ReceivedDataPacket(1) == 0x18)
                {
                    byte[] aa = {
                    m_TargetNode + 0x80,
                    0,
                    m_MyNode + 0x80
                };
                    IncrementTNS();
                    SendData(aa);
                    PacketOpened = true;
                    CommandInQueue = false;
                    //* Do not clear command until it is acknowledged, this forces continuous retries
                }

                //* Send back an Acknowledge of data received
                if (ReceivedDataPacket(1) > 0 & ReceivedDataPacket(1) != 0x18)
                {
                    byte[] a = {
                    m_TargetNode + 0x80,
                    0x18,
                    m_MyNode + 0x80
                };
                    IncrementTNS();
                    SendData(a);
                    PacketOpened = true;

                    if (ReceivedDataPacket[1] > 1)
                    {
                        if ((ReceivedDataPacket[1] & 31) == 0x8)
                        {
                            //Response = ResponseTypes.DataReturned
                            DF1DataLink1_DataReceived();
                        }
                    }
                }
                //* Token Passed to node
                if (ReceivedDataPacket[1] == 0)
                {
                    if (!CommandInQueue | PacketOpened)
                    {
                        byte[] a = {
                        m_TargetNode + 0x80,
                        0,
                        m_MyNode + 0x80
                    };
                        SendData(a);
                        PacketOpened = false;
                    }
                    else
                    {
                        byte[] QC = new byte[QueuedCommand.Count];
                        QueuedCommand.CopyTo(QC, 0);
                        SendData(QC);
                        //CommandInQueue = False '* Do not clear command until it is acknowledged, this forces continuous retries
                    }
                }
            }
        }

        /// <summary>
        /// Opens the comm port to start communications
        /// </summary>
        /// <returns></returns>
        /// <remarks></remarks>
        public object OpenComms()
        {
            //*****************************************
            //* Open serial port if not already opened
            //*****************************************
            if (!SerialPort.IsOpen)
            {
                SerialPort.BaudRate = m_BaudRate;
                SerialPort.PortName = m_ComPort;
                SerialPort.Parity = m_Parity;
                //SerialPort.Handshake = IO.Ports.Handshake.RequestToSend
                //SerialPort.ReadBufferSize = 16384
                SerialPort.ReceivedBytesThreshold = 1;
                //SerialPort.WriteBufferSize = 2048
                try
                {
                    SerialPort.Open();
                    SerialPort.DiscardInBuffer();
                    if (Protocol != "DF1")
                    {
                        SerialPort.DtrEnable = true;
                        SerialPort.RtsEnable = false;
                    }
                }
                catch (Exception ex)
                {
                    throw new DF1Exception("Failed To Open " + SerialPort.PortName + ". " + ex.Message);
                }

            }

            return 0;
        }

        //***************************************
        //* An attempt to initiate DH485 comms
        //***************************************
        private void SendStart()
        {
            OpenComms();
            byte[] a = {
            m_TargetNode + 0x80,
            0x2,
            m_MyNode + 0x80
        };
            //* This is an initial packet sent from RSLinx
            IncrementTNS();

            SendData(a);

            //m_TargetNode += 1

        }

        //*******************************************************************
        //* Send Data - this is the key entry used by the application layer
        //* A command stream in the form a a list of bytes are passed
        //* to this method. Protocol commands are then attached and
        //* then sent to the serial port
        //*******************************************************************
        private bool Acknowledged;
        private bool NotAcknowledged;
        private int AckWaitTicks;
        private bool[] ACKed = new bool[256];

        private int MaxSendRetries = 2;
        private int SendData(byte[] data)
        {
            //* A USB converer may need this
            //System.Threading.Thread.Sleep(50)

            //* Make sure there is data to send
            if (data.Length < 1)
                return -7;


            if (!SerialPort.IsOpen)
            {
                int OpenResult = OpenComms();
                if (OpenResult != 0)
                    return OpenResult;
            }


            //***************************************
            //* Calculate CheckSum of raw data
            //***************************************
            UInt16 CheckSumCalc = default(UInt16);
            if (m_CheckSum == CheckSumOptions.Crc)
            {
                CheckSumCalc = CalculateCRC16(data);
            }
            else
            {
                CheckSumCalc = CalculateBCC(data);
            }

            //***********************************************************
            //* Replace any 16's (DLE's) in the data string with a 16,16
            //***********************************************************
            int FirstDLE = Array.IndexOf(data, Convert.ToByte(16));
            if (FirstDLE >= 0)
            {
                int i = FirstDLE;
                // removed the -1 because the last byte could have been 16 30-AUG-07
                while (i < data.Length)
                {
                    if (data[i] == Convert.ToByte(16))
                    {
                        Array.Resize(ref data, data.Length + 1);
                        for (int j = data.Length - 1; j >= i + 1; j += -1)
                        {
                            data[j] = data[j - 1];
                        }
                        //data.Insert(i, 16)
                        i += 1;
                    }
                    i += 1;
                }
            }


            int ByteCount = data.Length + 5;

            //*********************************
            //* Attach STX, ETX and Checksum
            //*********************************
            byte[] BytesToSend = new byte[ByteCount + 1];
            BytesToSend[0] = 16;
            //* DLE
            BytesToSend[1] = 2;
            //* STX

            data.CopyTo(BytesToSend, 2);

            BytesToSend[ByteCount - 3] = 16;
            //* DLE
            BytesToSend[ByteCount - 2] = 3;
            //* ETX


            BytesToSend[ByteCount - 1] = (CheckSumCalc & 255);
            BytesToSend[ByteCount] = CheckSumCalc >> 8;



            //*********************************************
            //* Send the data and retry 3 times if failed
            //*********************************************
            //* Prepare for response and retries
            int Retries = 0;

            NotAcknowledged = true;
            Acknowledged = false;
            //While NotAcknowledged And Retries < 2
            //* Changed 18-FEB-08, ARJ
            while (!Acknowledged & Retries < MaxSendRetries)
            {
                if (m_Protocol != "DF1")
                {
                    SerialPort.RtsEnable = true;
                    SerialPort.DtrEnable = false;
                }


                //* Reset the response for retries
                Acknowledged = false;
                NotAcknowledged = false;

                //*******************************************************
                //* The stream of data is complete, send it now
                //* For those who want examples of data streams, put a
                //*  break point here nd watch the BytesToSend variable
                //*******************************************************
                SerialPort.Write(BytesToSend, 0, BytesToSend.Length);


                //System.Threading.Thread.Sleep(10)
                //* This is a test to try to get DH485 to work with PIC module
                if (m_Protocol != "DF1")
                {
                    SerialPort.RtsEnable = false;
                    SerialPort.DtrEnable = true;
                }


                //* Wait for response of a 1 second (50*20) timeout
                //* We only wait need to wait for an ACK
                //*  The PrefixAndSend Method will continue to wait for the data
                if (m_Protocol == "DF1")
                {
                    AckWaitTicks = 0;
                    while ((!Acknowledged & !NotAcknowledged) & AckWaitTicks < MaxTicks)
                    {
                        System.Threading.Thread.Sleep(20);
                        AckWaitTicks += 1;
                        //If Response = ResponseTypes.Enquire Then Response = ResponseTypes.NoResponse
                    }

                    //* TODO : check to see if NAK will cause a retry
                    if (NotAcknowledged | AckWaitTicks >= MaxTicks)
                    {
                        int DebugCheck = 0;
                    }
                }
                else
                {
                    //Response = ResponseTypes.Acknowledged
                }

                Retries += 1;
            }


            //**************************************
            //* Return a code indicating the status
            //**************************************
            if (Acknowledged)
            {
                return 0;
            }
            else if (NotAcknowledged)
            {
                return -2;
                //* Not Acknowledged
            }
            else
            {
                return -3;
                //* No Response
            }
            //Select Case Response
            //    Case ResponseTypes.Acknowledged : Return 0
            //    Case ResponseTypes.DataReturned : Return 0
            //    Case ResponseTypes.NotAcknowledged : Return -2
            //    Case ResponseTypes.NoResponse : Return -3
            //    Case Else : Return -4
            //End Select
        }

        /// <summary>
        /// Closes the comm port
        /// </summary>
        /// <remarks></remarks>
        public void CloseComms()
        {
            if (SerialPort.IsOpen)
            {
                //Try
                SerialPort.DiscardInBuffer();
                SerialPort.Close();
                //Catch ex As Exception
                //End Try
            }
        }

        //***********************************************
        //* Clear the buffer on a framing error
        //* This is an indication of incorrect baud rate
        //* If PLC is in DH485 mode, serial port throws
        //* an exception without this
        //***********************************************
        private void SerialPort_ErrorReceived(object sender, System.IO.Ports.SerialErrorReceivedEventArgs e)
        {
            if (e.EventType == System.IO.Ports.SerialError.Frame)
            {
                SerialPort.DiscardInBuffer();
            }
        }

        #endregion

    }


    //*************************************************
    //* Create an exception class for the DF1 class
    //*************************************************
    [SerializableAttribute()]
    public class DF1Exception : Exception
    {

        //* Use the resource manager to satisfy code analysis CA1303
        public DF1Exception() : this(new System.Resources.ResourceManager("en-US", System.Reflection.Assembly.GetExecutingAssembly()).GetString("DF1 Exception"))
        {
        }

        public DF1Exception(string message) : this(message, null)
        {
        }

        public DF1Exception(Exception innerException) : this(new System.Resources.ResourceManager("en-US", System.Reflection.Assembly.GetExecutingAssembly()).GetString("DF1 Exception"), innerException)
        {
        }

        public DF1Exception(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected DF1Exception(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context) : base(info, context)
        {
        }
    }
}

