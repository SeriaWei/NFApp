using Iot.Device.Ssd13xx;
using nanoFramework.Hardware.Esp32;
using nanoFramework.M2Mqtt.Messages;
using nanoFramework.M2Mqtt;
using nanoFramework.Networking;
using System;
using System.Device.Gpio;
using System.Device.I2c;
using System.Device.Wifi;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Device.Pwm;
using nanoFramework.Device.Bluetooth;
using nanoFramework.Device.Bluetooth.GenericAttributeProfile;

namespace NFApp
{
    public class Program
    {
        private static GpioController _gpioController;
        private static PwmChannel _led;
        private static GpioPin _ledPin;
        private static Ssd1306 _screen;
        private static MqttClient _client;
        private static bool _connectedToCloud;
        public static void Main()
        {
            Configuration.SetPinFunction(18, DeviceFunction.I2C1_DATA);
            Configuration.SetPinFunction(16, DeviceFunction.I2C1_CLOCK);
            Configuration.SetPinFunction(13, DeviceFunction.PWM1);
            _screen = new Ssd1306(I2cDevice.Create(new I2cConnectionSettings(1, Ssd1306.DefaultI2cAddress)), Ssd13xx.DisplayResolution.OLED128x64);

            _screen.Font = new BasicFont();
            _screen.ClearScreen();

            _gpioController = new GpioController();
            _led = PwmChannel.CreateFromPin(13, 40000, 0);
            _led.Stop();

            _ledPin = _gpioController.OpenPin(2, PinMode.Output);
            StartBluetooth();

            _screen.DrawString(0, 32, "Hello", 2, true);//centered text
            _screen.Display();

            while (true)
            {
                if (!_connectedToCloud)
                {
                    SetupAndConnectNetwork();
                    ConnectToBemfa();
                }
                else
                {
                    FlashTimes(1);
                }
                Thread.Sleep(1000);
            }
        }

        private static void ConnectToBemfa()
        {
            try
            {
                _client = new MqttClient("bemfa.com", 9501, false, null, null, MqttSslProtocols.None);
                _client.Connect("b656db78f2d642f1b74b0bb97203324a");
                // STEP 3: subscribe to topics you want
                _client.Subscribe(new[] { "nano002" }, new[] { MqttQoSLevel.AtLeastOnce });
                _client.MqttMsgPublishReceived += HandleIncomingMessage;
                _client.ConnectionClosed += Client_ConnectionClosed;
                //client.Publish("nano002/set", Encoding.UTF8.GetBytes("===== Hello MQTT! ====="), null, null, MqttQoSLevel.AtLeastOnce, false);
                _connectedToCloud = true;
            }
            catch
            {
                FlashTimes(3);
            }
        }

        private static void Client_ConnectionClosed(object sender, EventArgs e)
        {
            _connectedToCloud = false;
        }

        private static void HandleIncomingMessage(object sender, MqttMsgPublishEventArgs e)
        {
            string[] message = Encoding.UTF8.GetString(e.Message, 0, e.Message.Length).Split('#');

            if (message[0] == "on")
            {
                _led.Start();

                if (message.Length > 1 && int.TryParse(message[1], out int dutyCycle))
                {
                    _led.DutyCycle = dutyCycle / 100.0;
                }
                else
                {
                    _led.DutyCycle = 1;
                }
            }
            else
            {
                _led.Stop();
            }
            Debug.WriteLine($"Message received: {message}");
        }

        private static void SetupAndConnectNetwork()
        {
            const string wifiSsid = "ZKEASOFT";
            const string wifiPassword = "wkrlbh1314";

            var wifiAdapter = WifiAdapter.FindAllAdapters()[0];
            var ipAddress = NetworkInterface.GetAllNetworkInterfaces()[0].IPv4Address;
            var needToConnect = string.IsNullOrEmpty(ipAddress) || (ipAddress == "0.0.0.0");
            if (!needToConnect) return;

            while (true)
            {
                var result = wifiAdapter.Connect(wifiSsid, WifiReconnectionKind.Automatic, wifiPassword);

                if (result.ConnectionStatus == WifiConnectionStatus.Success)
                {
                    ipAddress = NetworkInterface.GetAllNetworkInterfaces()[0].IPv4Address;
                    Debug.WriteLine($"Connected to Wifi network with IP address {ipAddress}");
                    return;
                }
                else
                {
                    FlashTimes(2);
                    Thread.Sleep(1000);
                }
            }

        }
        private static void FlashTimes(int times)
        {
            for (int i = 0; i < times; i++)
            {
                _ledPin.Write(PinValue.High);
                Thread.Sleep(200);
                _ledPin.Write(PinValue.Low);
                Thread.Sleep(200);
            }
        }

        private static void StartBluetooth()
        {
            BluetoothLEServer server = BluetoothLEServer.Instance;

            server.DeviceName = "ESP32";

            // Define some custom Uuids
            Guid serviceUuid = new Guid("7A95DB3E-8942-440B-8578-10B6309092B9");
            Guid readCharUuid = new Guid("3936F426-17C0-4BB3-8C3B-3E7F33C1716C");

            //The GattServiceProvider is used to create and advertise the primary service definition.
            //An extra device information service will be automatically created.
            GattServiceProviderResult result = GattServiceProvider.Create(serviceUuid);
            if (result.Error != BluetoothError.Success)
            {
                return;
            }

            GattServiceProvider serviceProvider = result.ServiceProvider;

            GattLocalService service = serviceProvider.Service;
            GattLocalCharacteristicResult characteristicResult = service.CreateCharacteristic(readCharUuid,
                new GattLocalCharacteristicParameters()
                {
                    CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Write,
                    UserDescription = "My ESP32"
                });

            if (characteristicResult.Error != BluetoothError.Success)
            {
                return;
            }

            var readAndWriteCharacteristic = characteristicResult.Characteristic;

            readAndWriteCharacteristic.ReadRequested += ReadCharacteristic_ReadRequested;
            readAndWriteCharacteristic.WriteRequested += _readCharacteristic_WriteRequested;

            serviceProvider.StartAdvertising(new GattServiceProviderAdvertisingParameters()
            {
                IsConnectable = true,
                IsDiscoverable = true
            });
        }

        private static void _readCharacteristic_WriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs WriteRequestEventArgs)
        {
            GattWriteRequest request = WriteRequestEventArgs.GetRequest();
            if (request.Value.Length == 0)
            {
                request.Respond();
                return;
            }

            DataReader rdr = DataReader.FromBuffer(request.Value);
            byte[] bytes = new byte[request.Value.Length];
            rdr.ReadBytes(bytes);
            string s = Encoding.UTF8.GetString(bytes, 0, bytes.Length);
            _screen.ClearScreen();
            _screen.DrawString(0, 0, s);
            _screen.Display();
            if (request.Option == GattWriteOption.WriteWithResponse)
            {
                request.Respond();
            }
        }

        private static void ReadCharacteristic_ReadRequested(GattLocalCharacteristic sender, GattReadRequestedEventArgs ReadRequestEventArgs)
        {
            GattReadRequest request = ReadRequestEventArgs.GetRequest();
            DataWriter dw = new DataWriter();
            dw.WriteBoolean(true);
            request.RespondWithValue(dw.DetachBuffer());
        }
    }
}
