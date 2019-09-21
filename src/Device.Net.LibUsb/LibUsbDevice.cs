﻿using Device.Net.Exceptions;
using LibUsbDotNet;
using LibUsbDotNet.LudnMonoLibUsb;
using LibUsbDotNet.Main;
using LibUsbDotNet.WinUsb;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using usbnet = Usb.Net;
using Device.Net.LibUsb;

namespace Device.Net.LibUsb
{
    public class LibUsbInterfaceManager : usbnet.UsbInterfaceManager, usbnet.IUsbInterfaceManager
    {
        #region Fields
        private readonly SemaphoreSlim _WriteAndReadLock = new SemaphoreSlim(1, 1);
        private bool disposed;

        private ushort? _WriteBufferSize { get; }
        private ushort? _ReadBufferSize { get; }
        #endregion

        #region Public Properties
        public UsbDevice UsbDevice { get; }
        public int VendorId => GetVendorId(UsbDevice);
        public int ProductId => GetProductId(UsbDevice);
        public int Timeout { get; }
        public bool IsInitialized { get; private set; }
        public ushort WriteBufferSize => WriteUsbInterface.WriteEndpoint.MaxPacketSize;
        public ushort ReadBufferSize => ReadUsbInterface.ReadEndpoint.MaxPacketSize;
        #endregion

        #region Constructor
        public LibUsbInterfaceManager(UsbDevice usbDevice, int timeout, ILogger logger, ITracer tracer, ushort? writeBufferSize, ushort? readBufferSize) : base(logger, tracer)
        {
            UsbDevice = usbDevice;
            Timeout = timeout;

            _WriteBufferSize = writeBufferSize;
            _ReadBufferSize = readBufferSize;
        }
        #endregion

        #region Implementation
        public void Close()
        {
            UsbDevice?.Close();
        }

        public override void Dispose()
        {
            if (disposed) return;
            disposed = true;

            _WriteAndReadLock.Dispose();

            Close();

            base.Dispose();

            GC.SuppressFinalize(this);
        }

        public async Task InitializeAsync()
        {
            if (disposed) throw new ValidationException(Messages.DeviceDisposedErrorMessage);

            await Task.Run(() =>
            {

                //TODO: Error handling etc.
                UsbDevice.Open();

                //TODO: This is far beyond not cool.
                if (UsbDevice is MonoUsbDevice monoUsbDevice)
                {
                    monoUsbDevice.ClaimInterface(0);
                }
                else if (UsbDevice is WinUsbDevice winUsbDevice)
                {
                    //Doesn't seem necessary in this case...
                }
                else
                {
                    ((IUsbDevice)UsbDevice).ClaimInterface(0);
                }

                //Open the first read/write endpoints. TODO: This is dangerous
                var usbEndpointWriter = UsbDevice.OpenEndpointWriter(WriteEndpointID.Ep01);
                var usbEndpointReader = UsbDevice.OpenEndpointReader(ReadEndpointID.Ep01);

                //Get the buffer sizes
                var readBufferSize = _ReadBufferSize ?? (ushort)usbEndpointReader.EndpointInfo.Descriptor.MaxPacketSize;
                var writeBufferSize = _WriteBufferSize ?? (ushort)usbEndpointWriter.EndpointInfo.Descriptor.MaxPacketSize;

                //Create the endpoints
                var writeEndpoint = new WriteEndpoint(usbEndpointWriter, writeBufferSize);
                var readEndpoint = new ReadEndpoint(usbEndpointReader, readBufferSize);

                //Create an interface stub. LibUsbDotNet doesn't seem to allow for multiple interfaces? Or at least not allow for listing them
                var dummyInterface = new DummyInterface(Logger, Tracer, readBufferSize, writeBufferSize, Timeout);

                dummyInterface.UsbInterfaceEndpoints.Add(writeEndpoint);
                dummyInterface.UsbInterfaceEndpoints.Add(readEndpoint);

                //Set the default endpoints
                dummyInterface.ReadEndpoint = readEndpoint;
                dummyInterface.WriteEndpoint = writeEndpoint;

                UsbInterfaces.Add(dummyInterface);

                IsInitialized = true;
            });
        }

        public async Task<ReadResult> ReadAsync()
        {
            await _WriteAndReadLock.WaitAsync();

            try
            {
                return await ReadUsbInterface.ReadAsync(ReadBufferSize);
            }
            finally
            {
                _WriteAndReadLock.Release();
            }
        }

        public async Task WriteAsync(byte[] data)
        {
            await _WriteAndReadLock.WaitAsync();

            try
            {
                await WriteUsbInterface.WriteAsync(data);
            }
            finally
            {
                _WriteAndReadLock.Release();
            }
        }

        #endregion

        #region Public Static Methods
        public static int GetVendorId(UsbDevice usbDevice)
        {
            if (usbDevice is MonoUsbDevice monoUsbDevice)
            {
                return monoUsbDevice.Profile.DeviceDescriptor.VendorID;
            }
            return usbDevice.UsbRegistryInfo.Vid;
        }

        public static int GetProductId(UsbDevice usbDevice)
        {
            if (usbDevice is MonoUsbDevice monoUsbDevice)
            {
                return monoUsbDevice.Profile.DeviceDescriptor.ProductID;
            }
            return usbDevice.UsbRegistryInfo.Pid;
        }

        public Task<ConnectedDeviceDefinitionBase> GetConnectedDeviceDefinitionAsync()
        {
            var usbRegistryInfo = UsbDevice.UsbRegistryInfo;
            var result = usbRegistryInfo.ToConnectedDevice();
            return Task.FromResult<ConnectedDeviceDefinitionBase>(result);
        }
        #endregion
    }
}
