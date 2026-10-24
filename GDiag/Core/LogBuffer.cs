using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GDiag
{
    // Thread-safe TextWriter that collects whole lines so ImGui can draw them each frame.
    // Replaces the old TextboxWriter, which needed a WinForms TextBox and a timer.
    public class LogBuffer : TextWriter
    {
        const int MaxLines = 5000;

        readonly object WriteLock = new object();
        readonly List<string> Lines = new List<string>();
        StringBuilder PartialLine = new StringBuilder();

        // bumped on every completed line, lets the UI know when to auto-scroll
        public int Version { get; private set; }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (WriteLock)
            {
                if (value == '\n')
                {
                    CommitLine();
                }
                else if (value != '\r')
                {
                    PartialLine.Append(value);
                }
            }
        }

        public override void Write(string value)
        {
            if (value is null)
            {
                return;
            }
            foreach (char c in value)
            {
                Write(c);
            }
        }

        void CommitLine()
        {
            Lines.Add(PartialLine.ToString());
            PartialLine = new StringBuilder();
            if (Lines.Count > MaxLines)
            {
                Lines.RemoveRange(0, Lines.Count - MaxLines);
            }
            Version++;
        }

        // copies the current lines so the render thread never iterates a list that another thread is appending to
        public List<string> Snapshot()
        {
            lock (WriteLock)
            {
                var result = new List<string>(Lines);
                if (PartialLine.Length > 0)
                {
                    result.Add(PartialLine.ToString());
                }
                return result;
            }
        }

        public void Clear()
        {
            lock (WriteLock)
            {
                Lines.Clear();
                PartialLine = new StringBuilder();
                Version++;
            }
        }
    }
}
