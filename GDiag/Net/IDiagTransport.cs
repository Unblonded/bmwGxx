using System;

namespace GDiag.Net
{
    // one diagnostic message between the tester and a control unit
    public class DiagMessage
    {
        public byte Source;
        public byte Target;
        public byte[] Data;
        // set instead of Data when the gateway rejected the request (wrong address, not ready...)
        public string Error;
    }

    // a link to the car's gateway. The UDS layer above it matches requests and responses.
    public interface IDiagTransport : IDisposable
    {
        string Description { get; }
        bool IsOpen { get; }
        void Send(byte target, byte[] data);
        // the next message from the car, or null when none arrives in time
        DiagMessage Receive(int timeoutMs);
        // throws away anything that arrived late for an earlier request
        void DiscardPending();
    }

    public static class Addresses
    {
        // the address diagnostic testers use on BMW ENET (ISTA, E-Sys and EDIABAS all use it)
        public const byte Tester = 0xF4;
        // functional address: the gateway forwards the request to every control unit
        public const byte Functional = 0xDF;
        public const byte Gateway = 0x10;
    }
}
