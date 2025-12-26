using System;
using Steamworks;

namespace Crawlspace2MP
{
    /// <summary>
    /// Simple byte writer for network packets
    /// </summary>
    public class PacketWriter
    {
        private byte[] _buffer;
        private int _position;
        
        public PacketWriter(int initialCapacity = 256)
        {
            _buffer = new byte[initialCapacity];
            _position = 0;
        }
        
        public void Reset()
        {
            _position = 0;
        }
        
        public byte[] GetBytes()
        {
            var result = new byte[_position];
            Array.Copy(_buffer, result, _position);
            return result;
        }
        
        public int Length => _position;
        
        private void EnsureCapacity(int additionalBytes)
        {
            if (_position + additionalBytes > _buffer.Length)
            {
                var newBuffer = new byte[Math.Max(_buffer.Length * 2, _position + additionalBytes)];
                Array.Copy(_buffer, newBuffer, _position);
                _buffer = newBuffer;
            }
        }

        public void Put(byte value)
        {
            EnsureCapacity(1);
            _buffer[_position++] = value;
        }
        
        public void Put(bool value)
        {
            Put((byte)(value ? 1 : 0));
        }
        
        public void Put(int value)
        {
            EnsureCapacity(4);
            _buffer[_position++] = (byte)value;
            _buffer[_position++] = (byte)(value >> 8);
            _buffer[_position++] = (byte)(value >> 16);
            _buffer[_position++] = (byte)(value >> 24);
        }
        
        public void Put(long value)
        {
            var bytes = BitConverter.GetBytes(value);
            EnsureCapacity(8);
            Array.Copy(bytes, 0, _buffer, _position, 8);
            _position += 8;
        }
        
        public void Put(float value)
        {
            var bytes = BitConverter.GetBytes(value);
            EnsureCapacity(4);
            Array.Copy(bytes, 0, _buffer, _position, 4);
            _position += 4;
        }
        
        public void Put(double value)
        {
            var bytes = BitConverter.GetBytes(value);
            EnsureCapacity(8);
            Array.Copy(bytes, 0, _buffer, _position, 8);
            _position += 8;
        }
        
        public void Put(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                Put(0);
                return;
            }
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            Put(bytes.Length);
            EnsureCapacity(bytes.Length);
            Array.Copy(bytes, 0, _buffer, _position, bytes.Length);
            _position += bytes.Length;
        }
    }
    
    /// <summary>
    /// Simple byte reader for network packets (replaces LiteNetLib.NetPacketReader)
    /// </summary>
    public class PacketReader
    {
        private byte[] _buffer;
        private int _position;
        private int _length;
        
        public PacketReader(byte[] data)
        {
            _buffer = data;
            _position = 0;
            _length = data.Length;
        }
        
        public int AvailableBytes => _length - _position;
        
        public byte GetByte()
        {
            return _buffer[_position++];
        }
        
        public bool GetBool()
        {
            return GetByte() != 0;
        }
        
        public int GetInt()
        {
            int value = _buffer[_position] |
                       (_buffer[_position + 1] << 8) |
                       (_buffer[_position + 2] << 16) |
                       (_buffer[_position + 3] << 24);
            _position += 4;
            return value;
        }
        
        public long GetLong()
        {
            long value = BitConverter.ToInt64(_buffer, _position);
            _position += 8;
            return value;
        }
        
        public float GetFloat()
        {
            float value = BitConverter.ToSingle(_buffer, _position);
            _position += 4;
            return value;
        }
        
        public double GetDouble()
        {
            double value = BitConverter.ToDouble(_buffer, _position);
            _position += 8;
            return value;
        }
        
        public string GetString()
        {
            int length = GetInt();
            if (length == 0) return string.Empty;
            string value = System.Text.Encoding.UTF8.GetString(_buffer, _position, length);
            _position += length;
            return value;
        }
    }
}
