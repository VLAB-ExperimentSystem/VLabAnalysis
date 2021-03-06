﻿/*
RippleSignal.cs is part of the Experica.
Copyright (c) 2016 Li Alex Zhang and Contributors

Permission is hereby granted, free of charge, to any person obtaining a 
copy of this software and associated documentation files (the "Software"),
to deal in the Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the 
Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included 
in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF 
OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/
using UnityEngine;
using System.Collections.Immutable;
using System.Collections.Generic;
using System;
using System.Linq;
using Ripple;
using MathWorks.MATLAB.NET.Arrays;
using MathWorks.MATLAB.NET.Utility;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace Experica
{
    /// <summary>
    /// Ripple signal cached by internal thread through xippmex wrapper
    /// </summary>
    public class RippleSignal : ISignal
    {
        int disposecount = 0;
        int gotothreadevent = 0;
        readonly int digitalIPI, analogIPI, tickfreq, maxelectrodeid, timeunitpersec, sleepduration, diginbitchange;
        int _cachemaxduration;
        public int CacheMaxDuration
        {
            get { return Interlocked.CompareExchange(ref _cachemaxduration, 0, int.MinValue); }
            set { Interlocked.Exchange(ref _cachemaxduration, value); }
        }

        XippmexDotnet xippmexdotnet = new XippmexDotnet();
        ImmutableArray<int> _electrodeids = ImmutableArray.Create<int>();
        // Linear Data Buffers
        Dictionary<int, List<double>> spike;
        Dictionary<int, List<int>> uid;
        Dictionary<int, List<double>> dintime;
        Dictionary<int, List<int>> dinvalue;
        List<double[,]> lfp;
        List<double> lfpstarttime;

        readonly object xippmexlock = new object();
        readonly object apilock = new object();
        readonly object gotoeventlock = new object();
        Thread datathread = null;
        ManualResetEvent datathreadevent = new ManualResetEvent(false);
        bool _running = false;


        public RippleSignal(int tickfreq = 30000, int maxelectrodeid = 5120, int timeunitpersec = 1000,
            int digitalIPI = 800, int analogIPI = 4500, int sleepduration = 1, int cachemaxduration = 1800000, int diginbitchange = 1)
        {
            this.tickfreq = tickfreq;
            this.maxelectrodeid = maxelectrodeid;
            this.timeunitpersec = timeunitpersec;
            this.digitalIPI = digitalIPI;
            this.analogIPI = analogIPI;
            this.sleepduration = Math.Max(1, sleepduration);
            this.diginbitchange = diginbitchange;
            this._cachemaxduration = cachemaxduration;
            NewDataBuffer();
        }

        ~RippleSignal()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (1 == Interlocked.Exchange(ref disposecount, 1))
            {
                return;
            }
            lock (apilock)
            {
                Stop(false);
                datathread?.Abort();
                datathreadevent?.Dispose();
                Close();
                xippmexdotnet.Dispose();
            }
        }

        string SignalTypeToRipple(SignalType signaltype)
        {
            switch (signaltype)
            {
                case SignalType.Raw:
                    return "raw";
                case SignalType.ThirtyKSpS:
                    return "30ksps";
                case SignalType.TwoKSpS:
                    return "hi-res";
                case SignalType.OneKSpS:
                    return "1ksps";
                case SignalType.LFP:
                    return "lfp";
                case SignalType.Stim:
                    return "stim";
                default:
                    return "spk";
            }
        }

        SignalType RippleToSignalType(string ripplesignaltype)
        {
            string ripple;
            switch (ripplesignaltype)
            {
                case "raw":
                    ripple = "Raw";
                    break;
                case "30ksps":
                    ripple = "ThirtyKSpS";
                    break;
                case "hi-res":
                    ripple = "TwoKSpS";
                    break;
                case "1ksps":
                    ripple = "OneKSpS";
                    break;
                case "lfp":
                    ripple = "LFP";
                    break;
                case "stim":
                    ripple = "Stim";
                    break;
                default:
                    ripple = "Spike";
                    break;
            }
            return (SignalType)Enum.Parse(typeof(SignalType), ripple);
        }

        public SignalSource Source { get { return SignalSource.Ripple; } }

        public bool Connect()
        {
            bool r = false;
            try
            {
                lock (xippmexlock)
                {
                    r = ((MWLogicalArray)xippmexdotnet.xippmex(1)[0]).ToVector()[0];
                }
            }
            catch (Exception e) { Debug.LogException(e); }
            if (!r)
            {
                lock (apilock)
                {
                    _electrodeids = ImmutableArray.Create<int>();
                }
            }
            else
            {
                RefreshChannels();
            }
            return r;
        }

        public void Close()
        {
            lock (xippmexlock)
            {
                xippmexdotnet.xippmex("close");
            }
            lock (apilock)
            {
                _electrodeids = ImmutableArray.Create<int>();
            }
        }

        public void RefreshChannels()
        {
            try
            {
                double[] t = null;
                lock (xippmexdotnet)
                {
                    t = (double[])((MWNumericArray)xippmexdotnet.xippmex(1, "elec", "all")[0]).ToVector(MWArrayComponent.Real);
                }
                if (t != null)
                {
                    lock (apilock)
                    {
                        _electrodeids = t.Where(i => i <= maxelectrodeid).Select(i => (int)i).ToImmutableArray();
                    }
                    if (_electrodeids.Length > 0)
                    {
                        lock (xippmexlock)
                        {
                            xippmexdotnet.diginbitchange(new MWNumericArray(diginbitchange, true));
                        }
                    }
                }
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        public ImmutableArray<int> Channels { get { lock (apilock) { return _electrodeids; } } }

        public bool IsChannel { get { lock (apilock) { return _electrodeids.Length > 0; } } }

        public double Time
        {
            get
            {
                double t = double.NaN;
                try
                {
                    lock (xippmexlock)
                    {
                        t = ((MWNumericArray)xippmexdotnet.xippmex(1, "time")[0]).ToScalarDouble() / tickfreq * timeunitpersec;
                    }
                }
                catch (Exception e) { Debug.LogException(e); }
                return t;
            }
        }

        public ImmutableArray<SignalType> GetSignalTypes(int channel, bool onlyreturnsignalontype = true)
        {
            lock (apilock)
            {
                ImmutableArray<SignalType> sts = ImmutableArray.Create<SignalType>();
                if (IsChannel && Channels.Contains(channel))
                {
                    MWCellArray ts = null;
                    try
                    {
                        lock (xippmexlock)
                        {
                            ts = xippmexdotnet.xippmex(1, "signal", channel)[0] as MWCellArray;
                        }
                    }
                    catch (Exception e) { Debug.LogException(e); }
                    if (ts != null)
                    {
                        var tn = ts.NumberOfElements;
                        if (tn > 0)
                        {
                            var vs = new SignalType[tn];
                            for (var i = 0; i < tn; i++)
                            {
                                vs[i] = RippleToSignalType(((MWCharArray)ts[new[] { 1, i + 1 }]).ToString());
                            }
                            sts = vs.ToImmutableArray();
                        }
                    }
                }
                if (sts.Length > 0 && onlyreturnsignalontype)
                {
                    sts = sts.Where(i => IsSignalOn(channel, i)).ToImmutableArray();
                }
                return sts;
            }
        }

        public bool IsSignalOn(int channel, SignalType signaltype)
        {
            int r = 0;
            if (IsChannel && Channels.Contains(channel))
            {
                try
                {
                    lock (xippmexlock)
                    {
                        r = ((MWNumericArray)xippmexdotnet.xippmex(1, "signal", channel, SignalTypeToRipple(signaltype))[0]).ToScalarInteger();
                    }
                }
                catch (Exception e) { Debug.LogException(e); }
            }
            return r == 1 ? true : false;
        }

        public bool IsRunning { get { lock (apilock) { return _running; } } }

        public bool Start(bool isclean = true)
        {
            lock (apilock)
            {
                if (IsChannel)
                {
                    if (datathread == null)
                    {
                        datathread = new Thread(CollectDataThreadFunction)
                        {
                            Name = "RippleSignal"
                        };
                        datathreadevent.Set();
                        _running = true;
                        datathread.Start();
                    }
                    else
                    {
                        if (!_running)
                        {
                            if (isclean)
                            {
                                NewDataBuffer();
                            }
                            _running = true;
                            datathreadevent.Set();
                        }
                    }
                    return true;
                }
                else
                {
                    return false;
                }
            }
        }

        public bool Stop(bool collectallbeforestop = true)
        {
            lock (apilock)
            {
                if (datathread != null && _running)
                {
                    lock (gotoeventlock)
                    {
                        datathreadevent.Reset();
                        Interlocked.Exchange(ref gotothreadevent, 1);
                    }
                    while (true)
                    {
                        if (0 == Interlocked.CompareExchange(ref gotothreadevent, 0, int.MinValue))
                        {
                            if (collectallbeforestop)
                            {
                                // make sure to get all data until the time this function is called.
                                CollectData();
                            }
                            _running = false;
                            break;
                        }
                    }
                    return true;
                }
                else
                {
                    return false;
                }
            }
        }

        public bool Restart(bool iscleanall = true)
        {
            lock (apilock)
            {
                Stop(false);
                if (iscleanall)
                {
                    // Clear xippmex buffer
                    Close();
                    Connect();
                }
                return Start(true);
            }
        }

        #region Collect Data
        void CollectDataThreadFunction()
        {
            string fcs, scs;
            int fi, si, fn;
            if (digitalIPI < analogIPI)
            {
                fn = (int)Math.Floor((double)analogIPI / digitalIPI);
                fcs = "digital";
                scs = "analog";
                fi = digitalIPI;
                si = analogIPI % digitalIPI;
            }
            else if (digitalIPI == analogIPI)
            {
                fn = 1;
                fcs = "all";
                scs = "";
                fi = digitalIPI;
                si = 0;
            }
            else
            {
                fn = (int)Math.Floor((double)digitalIPI / analogIPI);
                fcs = "analog";
                scs = "digital";
                fi = analogIPI;
                si = digitalIPI % analogIPI;
            }
            while (true)
            {
                ThreadEvent:
                lock (gotoeventlock)
                {
                    Interlocked.Exchange(ref gotothreadevent, 0);
                    datathreadevent.WaitOne();
                }
                for (var i = 0; i < fn; i++)
                {
                    if (1 == Interlocked.CompareExchange(ref gotothreadevent, 0, 1))
                    {
                        goto ThreadEvent;
                    }
                    for (var j = 0; j < fi / sleepduration; j++)
                    {
                        Thread.Sleep(sleepduration);
                        if (1 == Interlocked.CompareExchange(ref gotothreadevent, 0, 1))
                        {
                            goto ThreadEvent;
                        }
                    }
                    switch (fcs)
                    {
                        case "digital":
                            CollectDigitalIn();
                            CollectSpike();
                            break;
                        case "analog":
                            CollectLFP();
                            break;
                        default:
                            CollectData();
                            break;
                    }
                }
                if (!string.IsNullOrEmpty(scs))
                {
                    if (1 == Interlocked.CompareExchange(ref gotothreadevent, 0, 1))
                    {
                        goto ThreadEvent;
                    }
                    for (var j = 0; j < si / sleepduration; j++)
                    {
                        Thread.Sleep(sleepduration);
                        if (1 == Interlocked.CompareExchange(ref gotothreadevent, 0, 1))
                        {
                            goto ThreadEvent;
                        }
                    }
                    switch (scs)
                    {
                        case "digital":
                            CollectDigitalIn();
                            CollectSpike();
                            break;
                        case "analog":
                            CollectLFP();
                            break;
                        default:
                            CollectData();
                            break;
                    }
                }
            }
        }

        void CollectData()
        {
            CollectDigitalIn();
            CollectSpike();
            CollectLFP();
        }

        void CollectDigitalIn()
        {
            MWArray[] d = null;
            try
            {
                lock (xippmexlock)
                {
                    d = xippmexdotnet.digin(3);
                }
            }
            catch (Exception e) { Debug.LogException(e); }
            if (d == null) return;

            var d1 = d[1] as MWCellArray;
            var d2 = d[2] as MWCellArray;
            for (var i = 1; i <= d1.NumberOfElements; i++)
            {
                var idx = new[] { i, 1 };
                var chdt = d1[idx] as MWNumericArray;
                var chdv = d2[idx] as MWNumericArray;
                if (!chdt.IsEmpty)
                {
                    if (!dintime.ContainsKey(i))
                    {
                        dintime[i] = new List<double>();
                        dinvalue[i] = new List<int>();
                    }
                    var cht = (double[])chdt.ToVector(MWArrayComponent.Real);
                    var chv = (double[])chdv.ToVector(MWArrayComponent.Real);
                    var chn = cht.Length;
                    for (var j = 0; j < chn; j++)
                    {
                        dintime[i].Add((cht[j] / tickfreq) * timeunitpersec);
                        dinvalue[i].Add((int)chv[j]);
                    }
                    var el = dintime[i].Count - CacheMaxDuration;
                    if (el > 0)
                    {
                        dintime[i].RemoveRange(0, el);
                        dinvalue[i].RemoveRange(0, el);
                    }
                }
            }
        }

        void CollectSpike()
        {
            var eids = Channels;
            MWArray[] s = null;
            try
            {
                lock (xippmexlock)
                {
                    // xippmex works only when electrode ids are in (1,n) row vector of doubles
                    s = xippmexdotnet.xippmex(4, "spike", new MWNumericArray(1, eids.Length, eids.ToArray(), null, true, false));
                }
            }
            catch (Exception e) { Debug.LogException(e); }
            if (s == null) return;

            var s1 = s[1] as MWCellArray;
            var s3 = s[3] as MWCellArray;
            for (var i = 0; i < s1.NumberOfElements; i++)
            {
                // Matlab indexing is 1-based
                var idx = new int[] { i + 1, 1 };
                var chst = s1[idx] as MWNumericArray;
                if (!chst.IsEmpty)
                {
                    var eid = eids[i];
                    if (!spike.ContainsKey(eid))
                    {
                        spike[eid] = new List<double>();
                        uid[eid] = new List<int>();
                    }
                    var chss = (double[])chst.ToVector(MWArrayComponent.Real);
                    var chus = (double[])(s3[idx] as MWNumericArray).ToVector(MWArrayComponent.Real);
                    var chn = chss.Length;
                    for (var j = 0; j < chn; j++)
                    {
                        spike[eid].Add((chss[j] / tickfreq) * timeunitpersec);
                        uid[eid].Add((int)chus[j]);
                    }
                    var el = spike[eid].Count - CacheMaxDuration;
                    if (el > 0)
                    {
                        spike[eid].RemoveRange(0, el);
                        uid[eid].RemoveRange(0, el);
                    }
                }
            }
        }

        void CollectLFP()
        {
        }
        #endregion

        public void Read(out Dictionary<int, List<double>> ospike, out Dictionary<int, List<int>> ouid,
            out List<double[,]> olfp, out List<double> olfpstarttime,
            out Dictionary<int, List<double>> odintime, out Dictionary<int, List<int>> odinvalue)
        {
            lock (apilock)
            {
                if (_running)
                {
                    Stop(true);
                    GetDataBuffer(out ospike, out ouid, out olfp, out olfpstarttime, out odintime, out odinvalue);
                    Start(false);
                }
                else
                {
                    GetDataBuffer(out ospike, out ouid, out olfp, out olfpstarttime, out odintime, out odinvalue);
                }
            }
        }

        void GetDataBuffer(out Dictionary<int, List<double>> ospike, out Dictionary<int, List<int>> ouid,
            out List<double[,]> olfp, out List<double> olfpstarttime,
            out Dictionary<int, List<double>> odintime, out Dictionary<int, List<int>> odinvalue)
        {
            ospike = spike;
            ouid = uid;
            olfp = lfp;
            olfpstarttime = lfpstarttime;
            odintime = dintime;
            odinvalue = dinvalue;
            NewDataBuffer();
        }

        void NewDataBuffer()
        {
            spike = new Dictionary<int, List<double>>();
            uid = new Dictionary<int, List<int>>();
            lfp = new List<double[,]>();
            lfpstarttime = new List<double>();
            dintime = new Dictionary<int, List<double>>();
            dinvalue = new Dictionary<int, List<int>>();
        }
    }
}