using CanBusTriple;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CanLogger
{
    class CanMessageBuffer
    {
        const int TIMEOUT_MS = 500;
        private CanMessage[] buffer;
        // Where the next message will be put
        private int putIndex;
        // Where the next message will be get (only if != putIndex)
        private int getIndex;
        private bool busy = false;
        private int timeoutWait = 0;

        public bool IsEmpty
        {
            get { return (this.putIndex == this.getIndex); }
        }

        public CanMessageBuffer(int bufferSize)
        {
            this.buffer = new CanMessage[bufferSize];
            this.getIndex = this.putIndex = 0;
        }

        public void AddMessage(CanMessage msg)
        {
            waitSemaphore();
            this.busy = true;
            this.buffer[this.putIndex++] = msg;
            if (this.putIndex == this.buffer.Length) this.putIndex = 0;
            if (this.putIndex == this.getIndex) {
                this.getIndex++;
                if (this.getIndex == this.buffer.Length) this.getIndex = 0;
            }
            this.busy = false;
        }

        public CanMessage[] GetLastMessages(int num)
        {
            waitSemaphore();
            this.busy = true;

            CanMessage[] msgs;
            if (this.putIndex == this.getIndex) {
                // No new messages
                msgs = new CanMessage[0];
            }
            else if (this.putIndex > this.getIndex || this.putIndex >= num) {
                // New messages in a single block
                var size = (this.putIndex > this.getIndex) ? Math.Min(this.putIndex - this.getIndex, num) : num;
                msgs = new CanMessage[size];
                Array.Copy(this.buffer, this.putIndex - size, msgs, 0, size);
                this.getIndex = this.putIndex;
            }
            else {
                // New messages in two blocks (putIndex < getIndex)
                // Block1: ??? --> last index, Block2: 0 --> (putIndex - 1)
                var sizeBlock1 = Math.Min(this.buffer.Length - this.getIndex, num - this.putIndex);
                msgs = new CanMessage[sizeBlock1 + this.putIndex];
                Array.Copy(this.buffer, this.buffer.Length - sizeBlock1, msgs, 0, sizeBlock1);
                Array.Copy(this.buffer, 0, msgs, sizeBlock1, this.putIndex);
                this.getIndex = this.putIndex;
            }
            this.busy = false;
            return msgs;
        }

        public void Clear()
        {
            waitSemaphore();
            this.getIndex = this.putIndex = 0;
        }

        private void waitSemaphore()
        {
            if (!this.busy) return;
            while (this.busy && this.timeoutWait < TIMEOUT_MS) {
                Thread.Sleep(10);
                this.timeoutWait += 10;
            }
            if (timeoutWait >= TIMEOUT_MS) throw new Exception("Operation timed out");
            this.timeoutWait = 0;
        }
    }
}
