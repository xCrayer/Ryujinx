using ARMeilleure.Memory;
using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.Exceptions;
using Ryujinx.HLE.HOS.Ipc;
using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostAsGpu;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostChannel;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostCtrl;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostCtrlGpu;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvMap;
using Ryujinx.HLE.HOS.Services.Nv.Types;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Ryujinx.HLE.HOS.Services.Nv
{
    [Service("nvdrv")]
    [Service("nvdrv:a")]
    [Service("nvdrv:s")]
    [Service("nvdrv:t")]
    class INvDrvServices : IpcService
    {
        private static Dictionary<string, Type> _deviceFileRegistry =
                   new Dictionary<string, Type>()
        {
                       { "/dev/nvmap",           typeof(NvMapDeviceFile)         },
                       { "/dev/nvhost-ctrl",     typeof(NvHostCtrlDeviceFile)    },
                       { "/dev/nvhost-ctrl-gpu", typeof(NvHostCtrlGpuDeviceFile) },
                       { "/dev/nvhost-as-gpu",   typeof(NvHostAsGpuDeviceFile)   },
                       { "/dev/nvhost-gpu",      typeof(NvHostGpuDeviceFile)     },
                     //{ "/dev/nvhost-msenc",    typeof(NvHostChannelDeviceFile) },
                       { "/dev/nvhost-nvdec",    typeof(NvHostChannelDeviceFile) },
                     //{ "/dev/nvhost-nvjpg",    typeof(NvHostChannelDeviceFile) },
                       { "/dev/nvhost-vic",      typeof(NvHostChannelDeviceFile) },
                     //{ "/dev/nvhost-display",  typeof(NvHostChannelDeviceFile) },
        };

        private static IdDictionary _deviceFileIdRegistry = new IdDictionary();

        private KProcess _owner;

        public INvDrvServices(ServiceCtx context)
        {
            _owner = null;
        }

        private int Open(ServiceCtx context, string path)
        {
            if (context.Process == _owner)
            {
                if (_deviceFileRegistry.TryGetValue(path, out Type deviceFileClass))
                {
                    ConstructorInfo constructor = deviceFileClass.GetConstructor(new Type[] { typeof(ServiceCtx) });

                    NvDeviceFile deviceFile = (NvDeviceFile)constructor.Invoke(new object[] { context });

                    return _deviceFileIdRegistry.Add(deviceFile);
                }
                else
                {
                    Logger.PrintWarning(LogClass.ServiceNv, $"Cannot find file device \"{path}\"!");
                }
            }

            return -1;
        }

        private NvResult GetIoctlArgument(ServiceCtx context, NvIoctl ioctlCommand, out Span<byte> arguments)
        {
            (long inputDataPosition,  long inputDataSize)  = context.Request.GetBufferType0x21(0);
            (long outputDataPosition, long outputDataSize) = context.Request.GetBufferType0x22(0);

            NvIoctl.Direction ioctlDirection = ioctlCommand.DirectionValue;
            uint              ioctlSize      = ioctlCommand.Size;

            bool isRead  = (ioctlDirection & NvIoctl.Direction.Read)  != 0;
            bool isWrite = (ioctlDirection & NvIoctl.Direction.Write) != 0;

            if ((isWrite && ioctlSize > outputDataSize) || (isRead && ioctlSize > inputDataSize))
            {
                arguments = null;

                Logger.PrintWarning(LogClass.ServiceNv, "Ioctl size inconsistency found!");

                return NvResult.InvalidSize;
            }

            if (isRead && isWrite)
            {
                if (outputDataSize < inputDataSize)
                {
                    arguments = null;

                    Logger.PrintWarning(LogClass.ServiceNv, "Ioctl size inconsistency found!");

                    return NvResult.InvalidSize;
                }

                byte[] outputData = new byte[outputDataSize];

                byte[] temp = context.Memory.ReadBytes(inputDataPosition, inputDataSize);

                Buffer.BlockCopy(temp, 0, outputData, 0, temp.Length);

                arguments = new Span<byte>(outputData);
            }
            else if (isWrite)
            {
                byte[] outputData = new byte[outputDataSize];

                arguments = new Span<byte>(outputData);
            }
            else
            {
                arguments = new Span<byte>(context.Memory.ReadBytes(inputDataPosition, inputDataSize));
            }

            return NvResult.Success;
        }

        private NvResult GetDeviceFileFromFd(int fd, out NvDeviceFile deviceFile)
        {
            deviceFile = null;

            if (fd < 0)
            {
                return NvResult.InvalidParameter;
            }

            deviceFile = _deviceFileIdRegistry.GetData<NvDeviceFile>(fd);

            if (deviceFile == null)
            {
                Logger.PrintWarning(LogClass.ServiceNv, $"Invalid file descriptor {fd}");

                return NvResult.NotImplemented;
            }

            if (deviceFile.Owner.Pid != _owner.Pid)
            {
                return NvResult.AccessDenied;
            }

            return NvResult.Success;
        }

        private NvResult EnsureInitialized()
        {
            if (_owner == null)
            {
                Logger.PrintWarning(LogClass.ServiceNv, "INvDrvServices is not initialized!");

                return NvResult.NotInitialized;
            }

            return NvResult.Success;
        }

        private static NvResult ConvertInternalErrorCode(NvInternalResult errorCode)
        {
            switch (errorCode)
            {
                case NvInternalResult.Success:
                    return NvResult.Success;
                case NvInternalResult.Unknown0x72:
                    return NvResult.AlreadyAllocated;
                case NvInternalResult.TimedOut:
                case NvInternalResult.TryAgain:
                case NvInternalResult.Interrupted:
                    return NvResult.Timeout;
                case NvInternalResult.InvalidAddress:
                    return NvResult.InvalidAddress;
                case NvInternalResult.NotSupported:
                case NvInternalResult.Unknown0x18:
                    return NvResult.NotSupported;
                case NvInternalResult.InvalidState:
                    return NvResult.InvalidState;
                case NvInternalResult.ReadOnlyAttribute:
                    return NvResult.ReadOnlyAttribute;
                case NvInternalResult.NoSpaceLeft:
                case NvInternalResult.FileTooBig:
                    return NvResult.InvalidSize;
                case NvInternalResult.FileTableOverflow:
                case NvInternalResult.BadFileNumber:
                    return NvResult.FileOperationFailed;
                case NvInternalResult.InvalidInput:
                    return NvResult.InvalidValue;
                case NvInternalResult.NotADirectory:
                    return NvResult.DirectoryOperationFailed;
                case NvInternalResult.Busy:
                    return NvResult.Busy;
                case NvInternalResult.BadAddress:
                    return NvResult.InvalidAddress;
                case NvInternalResult.AccessDenied:
                case NvInternalResult.OperationNotPermitted:
                    return NvResult.AccessDenied;
                case NvInternalResult.OutOfMemory:
                    return NvResult.InsufficientMemory;
                case NvInternalResult.DeviceNotFound:
                    return NvResult.ModuleNotPresent;
                case NvInternalResult.IoError:
                    return NvResult.ResourceError;
                default:
                    return NvResult.IoctlFailed;
            }
        }

        [Command(0)]
        // Open(buffer<bytes, 5> path) -> (s32 fd, u32 error_code)
        public ResultCode Open(ServiceCtx context)
        {
            NvResult errorCode = EnsureInitialized();
            int      fd        = -1;

            if (errorCode == NvResult.Success)
            {
                long pathPtr = context.Request.SendBuff[0].Position;

                string path = MemoryHelper.ReadAsciiString(context.Memory, pathPtr);

                fd = Open(context, path);

                if (fd == -1)
                {
                    errorCode = NvResult.FileOperationFailed;
                }
            }

            context.ResponseData.Write(fd);
            context.ResponseData.Write((uint)errorCode);

            return ResultCode.Success;
        }

        [Command(1)]
        // Ioctl(s32 fd, u32 ioctl_cmd, buffer<bytes, 0x21> in_args) -> (u32 error_code, buffer<bytes, 0x22> out_args)
        public ResultCode Ioctl(ServiceCtx context)
        {
            NvResult errorCode = EnsureInitialized();

            if (errorCode == NvResult.Success)
            {
                int     fd           = context.RequestData.ReadInt32();
                NvIoctl ioctlCommand = context.RequestData.ReadStruct<NvIoctl>();

                errorCode = GetIoctlArgument(context, ioctlCommand, out Span<byte> arguments);

                if (errorCode == NvResult.Success)
                {
                    errorCode = GetDeviceFileFromFd(fd, out NvDeviceFile deviceFile);

                    if (errorCode == NvResult.Success)
                    {
                        NvInternalResult internalResult = deviceFile.Ioctl(ioctlCommand, arguments);

                        if (internalResult == NvInternalResult.NotImplemented)
                        {
                            throw new NvIoctlNotImplementedException(context, deviceFile, ioctlCommand);
                        }

                        errorCode = ConvertInternalErrorCode(internalResult);

                        if ((ioctlCommand.DirectionValue & NvIoctl.Direction.Write) != 0)
                        {
                            context.Memory.WriteBytes(context.Request.GetBufferType0x22(0).Position, arguments.ToArray());
                        }
                    }
                }
            }

            context.ResponseData.Write((uint)errorCode);

            return ResultCode.Success;
        }

        [Command(2)]
        // Close(s32 fd) -> u32 error_code
        public ResultCode Close(ServiceCtx context)
        {
            NvResult errorCode = EnsureInitialized();

            if (errorCode == NvResult.Success)
            {
                int fd = context.RequestData.ReadInt32();

                errorCode = GetDeviceFileFromFd(fd, out NvDeviceFile deviceFile);

                if (errorCode == NvResult.Success)
                {
                    deviceFile.Close();

                    _deviceFileIdRegistry.Delete(fd);
                }
            }

            context.ResponseData.Write((uint)errorCode);

            return ResultCode.Success;
        }

        [Command(3)]
        // Initialize(u32 transfer_memory_size, handle<copy, process> current_process, handle<copy, transfer_memory> transfer_memory) -> u32 error_code
        public ResultCode Initialize(ServiceCtx context)
        {
            long transferMemSize   = context.RequestData.ReadInt64();
            int  transferMemHandle = context.Request.HandleDesc.ToCopy[0];

            _owner = context.Process;

            context.ResponseData.Write((uint)NvResult.Success);

            return ResultCode.Success;
        }

        [Command(4)]
        // QueryEvent(s32 fd, u32 event_id) -> (u32, handle<copy, event>)
        public ResultCode QueryEvent(ServiceCtx context)
        {
            NvResult errorCode = EnsureInitialized();

            if (errorCode == NvResult.Success)
            {
                int  fd      = context.RequestData.ReadInt32();
                uint eventId = context.RequestData.ReadUInt32();

                errorCode = GetDeviceFileFromFd(fd, out NvDeviceFile deviceFile);

                if (errorCode == NvResult.Success)
                {
                    NvInternalResult internalResult = deviceFile.QueryEvent(out int eventHandle, eventId);

                    if (internalResult == NvInternalResult.NotImplemented)
                    {
                        throw new NvQueryEventNotImplementedException(context, deviceFile, eventId);
                    }

                    errorCode = ConvertInternalErrorCode(internalResult);

                    if (errorCode == NvResult.Success)
                    {
                        context.Response.HandleDesc = IpcHandleDesc.MakeCopy(eventHandle);
                    }
                }
            }

            context.ResponseData.Write((uint)errorCode);

            return ResultCode.Success;
        }

        [Command(5)]
        // MapSharedMemory(s32 fd, u32 argument, handle<copy, shared_memory>) -> u32 error_code
        public ResultCode MapSharedMemory(ServiceCtx context)
        {
            NvResult errorCode = EnsureInitialized();

            if (errorCode == NvResult.Success)
            {
                int  fd                 = context.RequestData.ReadInt32();
                uint argument           = context.RequestData.ReadUInt32();
                int  sharedMemoryHandle = context.Request.HandleDesc.ToCopy[0];

                errorCode = GetDeviceFileFromFd(fd, out NvDeviceFile deviceFile);

                if (errorCode == NvResult.Success)
                {
                    KSharedMemory sharedMemory = context.Process.HandleTable.GetObject<KSharedMemory>(sharedMemoryHandle);

                    errorCode = ConvertInternalErrorCode(deviceFile.MapSharedMemory(sharedMemory, argument));
                }
            }

            context.ResponseData.Write((uint)errorCode);

            return ResultCode.Success;
        }

        [Command(6)]
        // GetStatus() -> (unknown<0x20>, u32 error_code)
        public ResultCode GetStatus(ServiceCtx context)
        {
            throw new ServiceNotImplementedException(context);
        }

        [Command(7)]
        // ForceSetClientPid(u64) -> u32 error_code
        public ResultCode ForceSetClientPid(ServiceCtx context)
        {
            throw new ServiceNotImplementedException(context);
        }

        [Command(8)]
        // SetClientPID(u64, pid) -> u32 error_code
        public ResultCode SetClientPid(ServiceCtx context)
        {
            long pid = context.RequestData.ReadInt64();

            context.ResponseData.Write(0);

            return ResultCode.Success;
        }

        [Command(9)]
        // DumpGraphicsMemoryInfo()
        public ResultCode DumpGraphicsMemoryInfo(ServiceCtx context)
        {
            Logger.PrintStub(LogClass.ServiceNv);

            return ResultCode.Success;
        }

        [Command(10)] // 3.0.0+
        // InitializeDevtools(u32, handle<copy>) -> u32 error_code;
        public ResultCode InitializeDevtools(ServiceCtx context)
        {
            throw new ServiceNotImplementedException(context);
        }

        [Command(11)] // 3.0.0+
        // Ioctl2(s32 fd, u32 ioctl_cmd, buffer<bytes, 0x21> in_args, buffer<bytes, 0x21> inline_in_buffer) -> (u32 error_code, buffer<bytes, 0x22> out_args)
        public ResultCode Ioctl2(ServiceCtx context)
        {
            NvResult errorCode = EnsureInitialized();

            if (errorCode == NvResult.Success)
            {
                int     fd           = context.RequestData.ReadInt32();
                NvIoctl ioctlCommand = context.RequestData.ReadStruct<NvIoctl>();

                (long inlineInBufferPosition, long inlineInBufferSize) = context.Request.GetBufferType0x21(1);

                errorCode = GetIoctlArgument(context, ioctlCommand, out Span<byte> arguments);

                Span<byte> inlineInBuffer = new Span<byte>(context.Memory.ReadBytes(inlineInBufferPosition, inlineInBufferSize));

                if (errorCode == NvResult.Success)
                {
                    errorCode = GetDeviceFileFromFd(fd, out NvDeviceFile deviceFile);

                    if (errorCode == NvResult.Success)
                    {
                        NvInternalResult internalResult = deviceFile.Ioctl2(ioctlCommand, arguments, inlineInBuffer);

                        if (internalResult == NvInternalResult.NotImplemented)
                        {
                            throw new NvIoctlNotImplementedException(context, deviceFile, ioctlCommand);
                        }

                        errorCode = ConvertInternalErrorCode(internalResult);

                        if ((ioctlCommand.DirectionValue & NvIoctl.Direction.Write) != 0)
                        {
                            context.Memory.WriteBytes(context.Request.GetBufferType0x22(0).Position, arguments.ToArray());
                        }
                    }
                }
            }

            context.ResponseData.Write((uint)errorCode);

            return ResultCode.Success;
        }

        [Command(12)] // 3.0.0+
        // Ioctl3(s32 fd, u32 ioctl_cmd, buffer<bytes, 0x21> in_args) -> (u32 error_code, buffer<bytes, 0x22> out_args,  buffer<bytes, 0x22> inline_out_buffer)
        public ResultCode Ioctl3(ServiceCtx context)
        {
            NvResult errorCode = EnsureInitialized();

            if (errorCode == NvResult.Success)
            {
                int     fd           = context.RequestData.ReadInt32();
                NvIoctl ioctlCommand = context.RequestData.ReadStruct<NvIoctl>();

                (long inlineOutBufferPosition, long inlineOutBufferSize) = context.Request.GetBufferType0x22(1);

                errorCode = GetIoctlArgument(context, ioctlCommand, out Span<byte> arguments);

                Span<byte> inlineOutBuffer = new Span<byte>(context.Memory.ReadBytes(inlineOutBufferPosition, inlineOutBufferSize));

                if (errorCode == NvResult.Success)
                {
                    errorCode = GetDeviceFileFromFd(fd, out NvDeviceFile deviceFile);

                    if (errorCode == NvResult.Success)
                    {
                        NvInternalResult internalResult = deviceFile.Ioctl3(ioctlCommand, arguments, inlineOutBuffer);

                        if (internalResult == NvInternalResult.NotImplemented)
                        {
                            throw new NvIoctlNotImplementedException(context, deviceFile, ioctlCommand);
                        }

                        errorCode = ConvertInternalErrorCode(internalResult);

                        if ((ioctlCommand.DirectionValue & NvIoctl.Direction.Write) != 0)
                        {
                            context.Memory.WriteBytes(context.Request.GetBufferType0x22(0).Position, arguments.ToArray());
                            context.Memory.WriteBytes(inlineOutBufferPosition, inlineOutBuffer.ToArray());
                        }
                    }
                }
            }

            context.ResponseData.Write((uint)errorCode);

            return ResultCode.Success;
        }

        [Command(13)] // 3.0.0+
        // FinishInitialize(unknown<8>)
        public ResultCode FinishInitialize(ServiceCtx context)
        {
            Logger.PrintStub(LogClass.ServiceNv);

            return ResultCode.Success;
        }

        public static void Destroy()
        {
            foreach (object entry in _deviceFileIdRegistry.Values)
            {
                NvDeviceFile deviceFile = (NvDeviceFile)entry;

                deviceFile.Close();
            }

            _deviceFileIdRegistry.Clear();
        }
    }
}