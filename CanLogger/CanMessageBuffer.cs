using System;
using System.Threading;
using CanBusTriple;

namespace CanLogger
{
    class CanMessageBuffer
    {
        const int TIMEOUT_MS = 500;
        private readonly CanMessage[] _buffer;
        // Where the next message will be put
        private int _putIndex;
        // Where the next message will be get (only if != putIndex)
        private int _getIndex;
        private bool _busy;
        private int _timeoutWait;

        public bool IsEmpty => (_putIndex == _getIndex);

        public CanMessageBuffer(int bufferSize)
        {
            _buffer = new CanMessage[bufferSize];
            _getIndex = _putIndex = 0;
        }

        public void AddMessage(CanMessage msg)
        {
            WaitSemaphore();
            _busy = true;
            _buffer[_putIndex++] = msg;
            if (_putIndex == _buffer.Length) _putIndex = 0;
            if (_putIndex == _getIndex) {
                _getIndex++;
                if (_getIndex == _buffer.Length) _getIndex = 0;
            }
            _busy = false;
        }

        public CanMessage[] GetLastMessages(int num)
        {
            WaitSemaphore();
            _busy = true;

            CanMessage[] msgs;
            if (_putIndex == _getIndex) {
                // No new messages
                msgs = new CanMessage[0];
            }
            else if (_putIndex > _getIndex || _putIndex >= num) {
                // New messages in a single block
                var size = (_putIndex > _getIndex) ? Math.Min(_putIndex - _getIndex, num) : num;
                msgs = new CanMessage[size];
                Array.Copy(_buffer, _putIndex - size, msgs, 0, size);
                _getIndex = _putIndex;
            }
            else {
                // New messages in two blocks (putIndex < getIndex)
                // Block1: ??? --> last index, Block2: 0 --> (putIndex - 1)
                var sizeBlock1 = Math.Min(_buffer.Length - _getIndex, num - _putIndex);
                msgs = new CanMessage[sizeBlock1 + _putIndex];
                Array.Copy(_buffer, _buffer.Length - sizeBlock1, msgs, 0, sizeBlock1);
                Array.Copy(_buffer, 0, msgs, sizeBlock1, _putIndex);
                _getIndex = _putIndex;
            }
            _busy = false;
            return msgs;
        }

        public void Clear()
        {
            WaitSemaphore();
            _getIndex = _putIndex = 0;
        }

        private void WaitSemaphore()
        {
            if (!_busy) return;
            while (_busy && _timeoutWait < TIMEOUT_MS) {
                Thread.Sleep(10);
                _timeoutWait += 10;
            }
            if (_timeoutWait >= TIMEOUT_MS) throw new Exception("Operation timed out");
            _timeoutWait = 0;
        }
    }
}
