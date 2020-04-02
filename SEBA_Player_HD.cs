const string ScreenName = "MainScreen";

IEnumerator<bool> LoaderStateMachine;

static char[] LCDMem;

static char[] GrayscaleTable = { (char)57600, (char)57673, (char)57746, (char)57819, (char)57892, (char)57965, (char)58038, (char)58111 };

static byte[] VideoData;

static StringBuilder LoadingInfo = new StringBuilder("");

void InsertNewLineChar()
{
	int i = 0;
	while (i < 216)
	{
		LCDMem[216 * i + (i > 0 ? (i - 1) : 0)] = (char)10;
		i++;
	}
}

void ResetLCDMem()
{
	LCDMem = new char[216 * 216 + 216];
	InsertNewLineChar();
}

#region Render

static int FrameNum, MemPtr, DataPtr;
static bool PlayFinished = false;

void ResetRender()
{
	ResetLCDMem();
	FrameNum = 1;
	MemPtr = DataPtr = 0;
	PlayFinished = false;
}

void RunRender()
{
	MemPtr = 5859;

	int PosX = 0;

	if (FrameNum % 2 == 0)
		MemPtr += 217;

	while (DataPtr < VideoData.Length)
	{
		char[] CurPixs = new char[4];

		byte PixByte = VideoData[DataPtr];
		if (PixByte == 0x80)
		{
			DataPtr += 2;
			break;
		}

		byte CountByte = VideoData[DataPtr + 1];

		byte Gray1 = (byte)((PixByte & 0xF0) >> 4);
		byte Gray2 = (byte)(PixByte & 0xF);

		char Pixel1 = GrayscaleTable[Gray1];
		char Pixel2 = GrayscaleTable[Gray2];

		int Count = 0;
		while (Count < CountByte + 1)
		{
			LCDMem[MemPtr] = Pixel1;
			LCDMem[MemPtr + 1] = Pixel2;

			MemPtr += 2;
			Count++;
			PosX++;

			if (PosX >= 108)
			{
				MemPtr += 218;
				PosX = 0;
			}
		}

		DataPtr += 2;
	}
	
	if (DataPtr >= VideoData.Length)
	{
		PlayFinished = true;
	}
	else
	{
		FrameNum ++ ;

		if (FrameNum % 2 == 1)
		{
			string LCDString = new String(LCDMem);
			WriteLCD(ScreenName, LCDString, 0.1f*(178f/216f), 0);
		}

		var InfoStr = $"Frame: {FrameNum}\n*DataPtr: {DataPtr}\n*MemPtr: {MemPtr}\n\nGlacc@bilibili";
		WriteLCD("InfoLCD", InfoStr, 2f, -1, "Debug");
	}
}

void WriteLCD(string LCDName, string Text, float Size = -1f, float TextPadding = -1f, string Font = "Monospace")
{
	var LCD = GridTerminalSystem.GetBlockWithName(LCDName) as IMyTextPanel;
	if (LCD != null)
	{
		LCD.ContentType = ContentType.TEXT_AND_IMAGE;
		LCD.Font = Font;
		if (Size > 0) LCD.FontSize = Size;
		if (TextPadding >= 0) LCD.TextPadding = TextPadding;
		LCD.WriteText(Text);
	}
	else
		Echo($"WriteLCD: {LCDName} is not exist.\n");
}

#endregion

#region Loader

static int BytesLoaded = 0;

static bool DecodeRunned = false;

static bool Loaded = false;

const int MaxLoopCount = 31000;

const float CmdFontSize = 0.8f;

#region Huffman
public static byte[] Data;
public static byte[] OriginalData;

public long DecodeProgress = 0;
public long DecodeSize = 0;

public bool DecodeFinished = false;

struct Branch
{
	public byte OrigByte;
	public int Frequency;
	public int LeftChild;
	public int RightChild;

	public Branch(byte InitByte, int InitFreq = 0, int InitLeft = -1, int InitRight = -1)
	{
		OrigByte = InitByte;
		Frequency = InitFreq;
		LeftChild = InitLeft;
		RightChild = InitRight;
	}
}

struct HuffmanCode
{
	public int[] Code;
	public HuffmanCode(int Leng)
	{
		Code = new int[Leng];
	}
}

const string HeaderFlag = "GlaccHuffmanEnc";

IEnumerator<bool> Decode()
{
	string Header = System.Text.Encoding.Default.GetString(OriginalData, 0, HeaderFlag.Length);
	Echo($"{HeaderFlag} F: {Header}");
	if (Header != HeaderFlag)
		throw new Exception("This is not .glh file.");

	List<Branch> BranchTable = new List<Branch>();

	int BranchCount = BitConverter.ToInt32(OriginalData, HeaderFlag.Length);

	int DataPtr = HeaderFlag.Length + 4;

	int i = 0;
	while (i < BranchCount)
	{
		byte OrigByte = OriginalData[DataPtr];
		int LeftChild = BitConverter.ToInt32(OriginalData, DataPtr + 1);
		int RightChild = BitConverter.ToInt32(OriginalData, DataPtr + 5);

		BranchTable.Add(new Branch(OrigByte, 0, LeftChild, RightChild));

		DataPtr += 9;
		i++;
	}

	long FileSize = DecodeSize = BitConverter.ToInt64(OriginalData, DataPtr);
	DecodeProgress = 0;

	int OriginalSize = BitConverter.ToInt32(OriginalData, DataPtr + 8);

	VideoData = new byte[OriginalSize];
	int OutputPtr = 0;

	Branch Root = BranchTable[BranchTable.Count - 1];
	Branch CurBranch = Root;

	DataPtr += 12;

	long BitCount = 0;

	int LoopCount = 0;

	yield return true;

	while (BitCount < FileSize)
	{
		byte Mask8 = (byte)(0x80 >> (int)(BitCount % 8));

		CurBranch = BranchTable[(OriginalData[DataPtr] & Mask8) != 0 ? CurBranch.RightChild : CurBranch.LeftChild];

		if (CurBranch.LeftChild == -1)
		{
			VideoData[OutputPtr++] = (byte)CurBranch.OrigByte;
			CurBranch = Root;
		}

		BitCount++;
		LoopCount++;
		if (BitCount % 8 == 0)
			DataPtr++;

		if (LoopCount > MaxLoopCount)
		{
			DecodeProgress = BitCount;
			yield return true;
			LoopCount = 0;
		}
	}

	DecodeFinished = true;
}
#endregion

IEnumerator<bool> LoadData()
{
	int LoopCount = 0;

	int BankNum = 0;

	int DataPtr = 0;

	int ReadPtr = 0;

	byte[] DataCache = new byte[25165824];

	LoadingInfo.Append("SE Video Player by Glacc\n216*162, 3-Bit Grayscale\n\nLoading data...\n");

	while (true)
	{
		IMyTextPanel DataModule = GridTerminalSystem.GetBlockWithName("Data_" + BankNum) as IMyTextPanel;
		if (DataModule == null) break;

		string[] DataArray = DataModule.CustomData.Split((char)10);
		int DataNum = 0;

		while (DataNum < DataArray.Length)
		{
			if (DataArray[DataNum] == "") break;

			char[] DataCharArr = DataArray[DataNum++].ToCharArray();

			DataPtr = 0;
			while (DataPtr < DataCharArr.Length)
			{
				string DataHexString = DataCharArr[DataPtr].ToString() + DataCharArr[DataPtr + 1].ToString();
				DataCache[ReadPtr++] = byte.Parse(DataHexString, System.Globalization.NumberStyles.HexNumber);

				DataPtr+=2;
				LoopCount++;
			}
		}

		if (LoopCount >= MaxLoopCount)
		{
			LoopCount = 0;
			BytesLoaded = ReadPtr;
			yield return true;
		}

		BankNum++;
	}

	int FinalSize = BytesLoaded = ReadPtr;
	OriginalData = new byte[FinalSize];

	DataPtr = 0;
	while (DataPtr < FinalSize)
	{
		OriginalData[DataPtr] = DataCache[DataPtr];
		DataPtr++;

		LoopCount++;
		if (LoopCount >= MaxLoopCount)
		{
			LoopCount = 0;
			yield return true;
		}
	}

	LoadingInfo.Append($"{FinalSize} Bytes loaded.\n");
}

void RunLoader()
{
	bool HasNextStep = LoaderStateMachine.MoveNext();

	if (!HasNextStep)
	{
		LoaderStateMachine.Dispose();
		if (!DecodeRunned)
		{
			LoadingInfo.Append("\nDecompressing...");
			WriteLCD(ScreenName, LoadingInfo.ToString(), CmdFontSize);

			LoaderStateMachine = Decode();

			DecodeRunned = true;

			Runtime.UpdateFrequency |= UpdateFrequency.Once;
		}
		else
		{
			LoadingInfo.Append($" 100%\n{VideoData.Length} Bytes\nDone.");
			WriteLCD(ScreenName, LoadingInfo.ToString(), CmdFontSize);
			Loaded = true;
		}
	}
	else
	{
		Runtime.UpdateFrequency |= UpdateFrequency.Once;
	}

	if (!DecodeRunned)
		WriteLCD(ScreenName, LoadingInfo.ToString() + $"{BytesLoaded} Bytes loaded.", CmdFontSize);

	if (!DecodeFinished && DecodeRunned && DecodeSize != 0)
		WriteLCD(ScreenName, LoadingInfo.ToString() + $" {Math.Round(DecodeProgress * 100.0 / DecodeSize)}%", CmdFontSize);
	
	WriteLCD("InfoLCD", "", 0);
}

#endregion

public Program()
{
	ResetRender();

	LoaderStateMachine = LoadData();

	Runtime.UpdateFrequency |= UpdateFrequency.Once;
}

public void Main(string Argument, UpdateType UpdateSource)
{
	switch (Argument.ToLower())
	{
		case "play":
			if (PlayFinished) ResetRender();
			if (Loaded) Runtime.UpdateFrequency = UpdateFrequency.Update1;
			break;
		case "stop":
			Runtime.UpdateFrequency = 0;
			ResetRender();
			break;
		case "pause":
			if (Loaded) Runtime.UpdateFrequency = 0;
			break;
	}

	if ((UpdateSource & (UpdateType.Terminal | UpdateType.Trigger | UpdateType.Script)) != 0 && !Loaded && Runtime.UpdateFrequency == 0)
		Runtime.UpdateFrequency |= UpdateFrequency.Once;

	if ((UpdateSource & UpdateType.Once) != 0 && !Loaded)
	{
		RunLoader();
	}

	if ((UpdateSource & UpdateType.Update1) != 0)
	{
		RunRender();
	}
}