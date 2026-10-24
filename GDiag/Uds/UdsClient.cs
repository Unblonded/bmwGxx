using System;
using System.Collections.Generic;
using System.Linq;
using GDiag.Net;

namespace GDiag.Uds
{
    public class UdsResponse
    {
        public byte Source;
        public byte[] Request;
        // full positive response including the service id + 0x40, or null
        public byte[] Data;
        public byte Nrc;
        public string Error;

        public bool Positive => Data != null;
        public bool TimedOut => Data is null && Nrc == 0 && Error is null;

        public string Describe()
        {
            if (Positive)
            {
                return Hex.Format(Data);
            }
            if (Nrc != 0)
            {
                return $"refused: {Nrcs.Describe(Nrc)} (0x{Nrc:X2})";
            }
            return Error ?? "no answer";
        }
    }

    // ISO 14229 (UDS) requests over any transport. One request at a time; the car answers in order.
    public class UdsClient
    {
        public const byte ReadDataByIdentifier = 0x22;
        public const byte WriteDataByIdentifier = 0x2E;
        public const byte ReadDtcInformation = 0x19;
        public const byte ClearDtcInformation = 0x14;
        public const byte SessionControl = 0x10;
        public const byte EcuReset = 0x11;
        public const byte TesterPresent = 0x3E;
        public const byte RoutineControl = 0x31;
        public const byte NegativeResponse = 0x7F;

        public readonly IDiagTransport Transport;
        readonly object RequestLock = new object();

        public int TimeoutMs = 1500;
        // after "response pending" (NRC 0x78) the control unit gets this long for the real answer
        public int PendingTimeoutMs = 6000;

        // every request and answer, for the trace panel
        public Action<string> Trace;

        public UdsClient(IDiagTransport transport)
        {
            Transport = transport;
        }

        public bool IsOpen => Transport.IsOpen;
        public string Description => Transport.Description;

        public UdsResponse Request(byte target, params byte[] request)
        {
            return Request(target, request, TimeoutMs);
        }

        public UdsResponse Request(byte target, byte[] request, int timeoutMs)
        {
            lock (RequestLock)
            {
                Transport.DiscardPending();
                Trace?.Invoke($"-> {target:X2}  {Hex.Format(request)}");
                Transport.Send(target, request);
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (true)
                {
                    int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0)
                    {
                        Trace?.Invoke($"<- {target:X2}  (no answer)");
                        return new UdsResponse { Source = target, Request = request };
                    }
                    var message = Transport.Receive(left);
                    if (message is null)
                    {
                        continue;
                    }
                    if (message.Error != null)
                    {
                        Trace?.Invoke($"<- {target:X2}  {message.Error}");
                        return new UdsResponse { Source = target, Request = request, Error = message.Error };
                    }
                    if (message.Source != target || message.Data.Length == 0)
                    {
                        // a late answer from some other control unit
                        continue;
                    }
                    Trace?.Invoke($"<- {message.Source:X2}  {Hex.Format(message.Data)}");
                    var response = Interpret(message, request);
                    if (response is null)
                    {
                        continue;
                    }
                    if (response.Nrc == Nrcs.ResponsePending)
                    {
                        deadline = DateTime.UtcNow.AddMilliseconds(PendingTimeoutMs);
                        continue;
                    }
                    return response;
                }
            }
        }

        // sends to the functional address and collects answers from every control unit for windowMs
        public List<UdsResponse> RequestFunctional(byte[] request, int windowMs = 2500)
        {
            lock (RequestLock)
            {
                Transport.DiscardPending();
                Trace?.Invoke($"-> {Addresses.Functional:X2}  {Hex.Format(request)}  (all control units)");
                Transport.Send(Addresses.Functional, request);
                var answers = new Dictionary<byte, UdsResponse>();
                var deadline = DateTime.UtcNow.AddMilliseconds(windowMs);
                while (true)
                {
                    int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0)
                    {
                        break;
                    }
                    var message = Transport.Receive(left);
                    if (message?.Data is null || message.Data.Length == 0)
                    {
                        continue;
                    }
                    Trace?.Invoke($"<- {message.Source:X2}  {Hex.Format(message.Data)}");
                    var response = Interpret(message, request);
                    if (response != null && response.Nrc != Nrcs.ResponsePending)
                    {
                        answers[message.Source] = response;
                    }
                }
                return answers.Values.OrderBy(a => a.Source).ToList();
            }
        }

        // null when the message doesn't belong to this request
        static UdsResponse Interpret(DiagMessage message, byte[] request)
        {
            byte[] data = message.Data;
            if (data[0] == NegativeResponse && data.Length >= 3 && data[1] == request[0])
            {
                return new UdsResponse { Source = message.Source, Request = request, Nrc = data[2] };
            }
            if (data[0] == (byte)(request[0] + 0x40))
            {
                return new UdsResponse { Source = message.Source, Request = request, Data = data };
            }
            return null;
        }

        // ---------------------------------------------------------------- common services

        // the data record of a DID without the 62 xx xx echo, or null
        public byte[] ReadDid(byte target, ushort did, out UdsResponse response)
        {
            response = Request(target, ReadDataByIdentifier, (byte)(did >> 8), (byte)did);
            if (!response.Positive || response.Data.Length < 3 || Hex.BigEndian(response.Data, 1, 2) != did)
            {
                return null;
            }
            return response.Data.Skip(3).ToArray();
        }

        public byte[] ReadDid(byte target, ushort did) => ReadDid(target, did, out _);

        // report DTCs by status mask (19 02). Returns null when the control unit didn't answer.
        public List<Dtc> ReadDtcs(byte target, out UdsResponse response, byte statusMask = Dtc.FaultMask)
        {
            response = Request(target, new byte[] { ReadDtcInformation, 0x02, statusMask }, 4000);
            if (!response.Positive)
            {
                return null;
            }
            return Dtc.ParseReport(response.Data, target);
        }

        public UdsResponse ClearAllDtcs(byte target)
        {
            return Request(target, new byte[] { ClearDtcInformation, 0xFF, 0xFF, 0xFF }, 8000);
        }

        public UdsResponse StartSession(byte target, byte session)
        {
            return Request(target, SessionControl, session);
        }
    }

    public static class Nrcs
    {
        public const byte ResponsePending = 0x78;

        static readonly Dictionary<byte, string> Names = new Dictionary<byte, string>
        {
            [0x10] = "general reject",
            [0x11] = "service not supported",
            [0x12] = "sub-function not supported",
            [0x13] = "wrong message length or format",
            [0x14] = "response too long",
            [0x21] = "busy, try again",
            [0x22] = "conditions not correct (ignition, engine running, speed...)",
            [0x24] = "request sequence error",
            [0x25] = "no response from sub-net component",
            [0x26] = "failure prevents execution",
            [0x31] = "request out of range (not supported by this control unit)",
            [0x33] = "security access denied",
            [0x35] = "invalid key",
            [0x36] = "exceeded number of attempts",
            [0x37] = "required time delay not expired",
            [0x70] = "upload/download not accepted",
            [0x71] = "transfer data suspended",
            [0x72] = "general programming failure",
            [0x73] = "wrong block sequence counter",
            [0x78] = "response pending",
            [0x7E] = "sub-function not supported in this session",
            [0x7F] = "service not supported in this session",
            [0x81] = "engine speed too high",
            [0x82] = "engine speed too low",
            [0x83] = "engine is running",
            [0x84] = "engine is not running",
            [0x88] = "vehicle speed too high",
            [0x8D] = "transmission not in park/neutral",
            [0x92] = "voltage too high",
            [0x93] = "voltage too low",
        };

        public static string Describe(byte nrc) => Names.TryGetValue(nrc, out var name) ? name : "unknown reason";
    }

    // services that only read or keep the session alive, safe to send without asking
    public static class ServiceSafety
    {
        public static bool IsReadOnly(byte[] request)
        {
            if (request is null || request.Length == 0)
            {
                return false;
            }
            switch (request[0])
            {
                case 0x22: // read data by identifier
                case 0x19: // read DTC information
                case 0x3E: // tester present
                case 0x23: // read memory by address
                case 0x24: // read scaling data
                    return true;
                case 0x10: // the default session is harmless, the others are not
                    return request.Length >= 2 && (request[1] & 0x7F) == 0x01;
                default:
                    return false;
            }
        }
    }
}
