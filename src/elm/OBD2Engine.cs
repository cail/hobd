﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Globalization;
using System.Threading;

namespace hobd
{

/// <summary>
/// OBD2Engine implements ELM327 protocol interfac for sensor fetching 
/// </summary>
public class OBD2Engine : Engine
{
    private bool thread_active = false;
    private long lastReceiveTS;
    Thread worker;

    int currentSensorIndex = -1;
    SensorListener currentSensorListener = null;
    long[] nextReadings = null;

    byte[] buffer = new byte[1024];
    int position = 0;
    int LinesSent;

    List<string> extraInitCommands = new List<string>();
    int extraInitIndex;
    int SensorInitIndex;
    
    public string VersionInfo = "";
    public int ProtocolId{get; protected set;}

    public const int ErrorThreshold = 10;
    bool CriticalError = false;

    public int ReadDelay = 0;
    
    public const string ST_INIT_HW = "INITHW";
    public const string ST_INIT = "INIT";
    public const string ST_ATZ = "ATZ";
    public const string ST_ATE0 = "ATE0";
    public const string ST_ATL0 = "ATL0";
    public const string ST_EXTRAINIT = "EXTRAINIT";
    public const string ST_SENSOR_INIT = "SENSOR_INIT";
    public const string ST_QUERY_PROTOCOL = "QUERY_PROTOCOL";
    public const string ST_SENSOR = "SENSOR";
    public const string ST_SENSOR_ACK = "SENSOR_ACK";
    public const string ST_ERROR_SOFT = "ERROR_SOFT";
    public const string ST_ERROR = "ERROR";

    string[] dataErrors = new string[]{ "NO DATA", "DATA ERROR", };
    // Error messages to immediately reset ELM
    string[] criticalErrors = new string[]{ "ELM327", "BUS BUSY", "BUS INIT: ERROR", "BUS INIT: .ERROR", "BUS INIT: ..ERROR", "BUS INIT: ...ERROR", "BUS ERROR", "CAN ERROR", "LV RESET", "UNABLE TO CONNECT" };

    int subsequentErrors = 0;

    public string State {get; private set;}
    
    public OBD2Engine()
    {
    }
    
    public override void Activate()
    {
        base.Activate();

        if (worker == null){
            worker = new Thread(this.Run);
            worker.Start();
        }
    }
    
    void PurgeStream()
    {
        Thread.Sleep(50);
        while(stream.HasData())
        {
            var buf = stream.Read();
            // hack against infinite null stream
            if (buf == null) break;
        }
    }
    
    int SendCommand(string command)
    {
        if (Logger.TRACE) Logger.trace("OBD2Engine", "SendCommand:" + command);
        var realcmd = command+"\r";
        int csize = realcmd.Count(c => c == '\r');
        byte[] arr = Encoding.ASCII.GetBytes(realcmd);
        stream.Write(arr, 0, arr.Length);
        return csize;
    }
    void SendRaw(string command)
    {
        if (Logger.TRACE) Logger.trace("OBD2Engine", "SendRaw:" + command);
        byte[] arr = Encoding.ASCII.GetBytes(command);
        stream.Write(arr, 0, arr.Length);
    }
        
    void SetState(string state2)
    {
        
        State = state2;
        StateDetails = state2;
        lastReceiveTS = DateTimeMs.Now;
        
        if (Logger.TRACE) Logger.trace("OBD2Engine", " -> " + State);

        switch(State)
        {
            case ST_SENSOR:
                fireStateNotify(STATE_READ);
                break;
            case ST_SENSOR_ACK:
                fireStateNotify(STATE_READ_DONE);
                break;
            case ST_ERROR:
            case ST_ERROR_SOFT:
                fireStateNotify(STATE_ERROR);
                break;                
            default:
                fireStateNotify(STATE_INIT);
                break;
        }
        
        switch(State){
            case ST_INIT_HW:
                Error = null;
                try{
                    stream.Close();
                    Logger.info("OBD2Engine", "Open "+url);
                    Thread.Sleep(100);
                    stream.Open(url);
                }catch(Exception e){
                    Error = e.Message;
                    Logger.error("OBD2Engine", "Init Error", e);
                    SetState(ST_ERROR);
                    break;
                }
                PurgeStream();
                SetState(ST_INIT);
                break;
            case ST_INIT:
                extraInitCommands.Clear();
                extraInitIndex = 0;
                if (CriticalError)
                {
                    CriticalError = false;
                    // trigger protocol autosearch TODO: conflicts with some settings, f.e. ATFI setup
                    //extraInitCommands.Add("ATSP 0");
                }
                if (initData != null)
                {
                    initData.Split(new char[]{';'}).ToList().ForEach((s) => {
                        var cmd = s.Trim();
                        if (cmd.Length > 0)
                            extraInitCommands.Add(cmd);
                    });
                }

                SensorInitIndex = 0;
                SetState(ST_ATZ);
                break;
            case ST_ATZ:
                SendCommand("ATZ");
                break;
            case ST_ATE0:
                SendCommand("ATE0");
                break;
            case ST_ATL0:
                SendCommand("ATL0");
                break;
            case ST_EXTRAINIT:
                if (extraInitIndex >= extraInitCommands.Count())
                {
                    SetState(ST_SENSOR_INIT);
                }else{
                    SendCommand(extraInitCommands[extraInitIndex]);
                    StateDetails = State + " " + this.extraInitCommands[this.extraInitIndex];
                    extraInitIndex++;
                }
                break;
            case ST_SENSOR_INIT:
                SendCommand("01" + SensorInitIndex.ToString("X2"));
                break;
            case ST_QUERY_PROTOCOL:
                SendCommand("ATDPN");
                break;
            case ST_SENSOR:

                var sls = Registry.ActiveSensors;
                
                if (sls.Length == 0)
                {
                    if (Logger.TRACE) Logger.trace("OBD2Engine", " no active sensors ");
                    break;
                }
                
                currentSensorIndex++;
                if (currentSensorIndex >= sls.Length)
                    currentSensorIndex = 0;
                
                int scanSensorIndex = currentSensorIndex;
                
                while (true)
                {
                        
                    currentSensorListener = sls[currentSensorIndex];
                    
                    // recreate reading timers if layout was changed!
                    if (nextReadings == null || nextReadings.Length != sls.Length){
                        nextReadings = new long[sls.Length];
                    }
                    long nextReading = nextReadings[currentSensorIndex];
                    
                    if (nextReading == 0 || nextReading <= DateTimeMs.Now)
                    {
                        if (currentSensorListener.sensor is OBD2Sensor){
                            if (Logger.TRACE) Logger.trace("OBD2Engine", " ----> " + currentSensorListener.sensor.ID);
                            var osensor = (OBD2Sensor)currentSensorListener.sensor;
                            var cmd = osensor.RawCommand;
                            if (cmd != null)
                            {
                                LinesSent = SendCommand(cmd);
                                SetState(ST_SENSOR_ACK);
                                break;
                            }else{
                                // move to next sensor
                            }
                        }
                    }else{
                        if (Logger.DUMP) Logger.dump("OBD2Engine", " Skipped " + currentSensorListener.sensor.ID + " with "+ (nextReading - DateTimeMs.Now));
                    }
                    
                    currentSensorIndex++;
                    if (currentSensorIndex >= sls.Length)
                        currentSensorIndex = 0;
                    if (currentSensorIndex == scanSensorIndex)
                        break;
                }
                break;
        }
    }
        
    void HandleReply(byte[] msg)
    {
        string smsg = Encoding.ASCII.GetString(msg, 0, msg.Length);
        if (Logger.TRACE) Logger.trace("OBD2Engine", "HandleReply: " + smsg.Trim());
        
        switch(State){
            case ST_INIT:
                break;
            case ST_ATZ:
                if (smsg.Contains("ATZ") || smsg.Contains("ELM"))
                {
                    VersionInfo = smsg.Replace("ATZ", "").Replace("\r", "").Replace("\n", "").Trim();
                    Logger.log("INFO", "OBD2Engine", "VersionInfo: " + VersionInfo, null);
                    if (VersionInfo.Length > 2)
                    {
                        criticalErrors[0] = VersionInfo;
                    }
                    SetState(ST_ATE0);
                }else{
                    SendCommand("ATZ");
                }
                break;
            case ST_ATE0:
                if (smsg.Contains("OK"))
                {
                    SetState(ST_ATL0);
                }
                break;
            case ST_ATL0:
                if (smsg.Contains("OK"))
                {
                    SetState(ST_EXTRAINIT);
                }
                break;
            case ST_EXTRAINIT:
                SetState(ST_EXTRAINIT);
                break;
            case ST_SENSOR_INIT:
                Error = criticalErrors.FirstOrDefault(e => smsg.Contains(e));
                if (Error != null) {
                    Logger.error("OBD2Engine", "Critical error on sensor init:" + smsg);
                    // Wait and then do soft reconnect
                    SetState(ST_ERROR_SOFT);
                    StateDetails = State + " " + Error;
                }else{
                    Logger.log("INFO", "OBD2Engine", "Sensor Init:" + smsg, null);
                    //PIDSupported.SetValue(msg);
                    SetState(ST_QUERY_PROTOCOL);
                }
                break;
            case ST_QUERY_PROTOCOL:
                try{
                    var proto = smsg.Replace("A", "");
                    ProtocolId = int.Parse(proto, NumberStyles.HexNumber);
                }catch(Exception){
                    Logger.error("OBD2Engine", "protocol "+smsg);
                }
                Logger.log("INFO", "OBD2Engine", "ProtocolId: " + ProtocolId, null);
                Registry.ProtocolId = ProtocolId;
                SetState(ST_SENSOR);
                break;
            case ST_SENSOR_ACK:
                // saving local copy
                var lsl = currentSensorListener;

                var osensor = (OBD2Sensor)lsl.sensor;

                nextReadings[currentSensorIndex] = DateTimeMs.Now + lsl.period + (1000*Math.Min(10, lsl.failures));
                
                // proactively read next sensor!
                SetState(ST_SENSOR);

                // valid reply - set value, raise listeners
                if (osensor.SetRawValue(msg))
                {
                    subsequentErrors = 0;
                    lsl.failures = 0;
                    this.Error = null;
                }else{
                    // search for known errors, increment counters
                    string error = dataErrors.FirstOrDefault(e => smsg.Contains(e));
                    if (error != null)
                    {
                        this.Error = this.Error == null ? error : this.Error + " " + error;
                        // increase period for this 'bad' sensor
                        if (subsequentErrors == 0)
                        {
                            Logger.info("OBD2Engine", "sensor not responding, increasing period: "+osensor.ID);
                            // !!!! VAZ.Core fail otherwise
                            lsl.failures++;
                            //lsl.period = unchecked((lsl.period +100) * 2);
                        }
                        subsequentErrors++;
                    }else{
                        error = criticalErrors.FirstOrDefault(e => smsg.Contains(e));
                        if (error != null) {
                            this.Error = error;
                            this.CriticalError = true;
                            Logger.error("OBD2Engine", "Critical error:" + smsg);
                            SetState(ST_ERROR_SOFT);
                            subsequentErrors = 0;
                        }
                    }
                }
                // act on too much errors
                if (subsequentErrors > ErrorThreshold) {
                    Logger.error("OBD2Engine", "Connection error threshold");
                    this.Error = "Connection error threshold";
                    subsequentErrors = 0;
                    this.CriticalError = true;
                    SetState(ST_ERROR_SOFT);
                }
                break;
        }
    }
        
    public int SleepOnError = 500;

    void HandleState()
    {
        
        if (State == ST_ERROR)
        {
            Thread.Sleep(SleepOnError);
            return;
        }

        // Means no sensor reading was performed - we have
        // to wait and search for another sensor
        if (State == ST_SENSOR)
        {
            Thread.Sleep(50);
            SetState(ST_SENSOR);
            return;
        }
        
        if (stream.HasData())
        {
            byte[] data = stream.Read();
            if (data != null)
            {
                if (position + data.Length < buffer.Length)
                {
                    Array.Copy(data, 0, buffer, position, data.Length);
                    position = position + data.Length;
                }else{
                    Logger.error("OBD2Engine", "BUFFER OVERFLOW! " + position+data.Length);
                    position = 0;
                }
                if (Logger.DUMP) Logger.dump("OBD2Engine", "BUFFER: "+Encoding.ASCII.GetString(buffer, 0, position));
                if (ReadDelay > 0)
                {
                    if (Logger.TRACE) Logger.trace("OBD2Engine", "Sleeping "+ReadDelay+" ms");
                    Thread.Sleep(ReadDelay);
                }
                if (data.Length > 0)
                    lastReceiveTS = DateTimeMs.Now;
            }
            data = null;
        }

        // nothing to read -  wait
        if (position == 0)
        {
            Thread.Sleep(50);
            return;
        }
        
        for(int isearch = 0; isearch < position; isearch++)
        {
            // end of reply found
            if (buffer[isearch] == '>'){
                byte[] msg = new byte[isearch];
                Array.Copy(buffer, 0, msg, 0, isearch);
                isearch++;
                Array.Copy(buffer, isearch, buffer, 0, position-isearch);
                position = position-isearch;
                // handle our extracted message
                HandleReply(msg);
                break;
            }
        }
    }

    public int PingTimeout = 1000;
    public int NoResponseTimeout = 5000;
    
    void Run()
    {
        thread_active = true;
        
        if (stream == null)
        {
            SetState(ST_ERROR);
            return;
        }
        SetState(ST_INIT_HW);
        
        while(this.active){
        
            try{
                HandleState();
            }catch(Exception e){
                Logger.error("OBD2Engine", "Run exception", e);
                SetState(ST_ERROR);
            }
            
            // No reply. Ping the connection.
            /*
            if (DateTimeMs.Now - lastReceiveTS > PingTimeout && State != ST_ERROR) {
                Logger.trace("OBD2Engine", "No reply. PING???");
                SendCommand("AT");
                // Only OBDSim bugs??
                SendRaw(" ");
                lastReceiveTS = DateTimeMs.Now;
            }
            */
            // Restart the hanged connection after N seconds
            var diff_ms = DateTimeMs.Now - lastReceiveTS;
            if (diff_ms > NoResponseTimeout) {
                // If ERROR, wait for a longer period before retrying
                if (this.State == ST_SENSOR || this.State == ST_SENSOR_ACK)
                {
                    SetState(ST_INIT_HW);
                }
                else if (this.State == ST_ERROR_SOFT)
                {
                    SetState(ST_INIT);
                }
                else if (diff_ms > ReconnectTimeout)
                {
                    SetState(ST_INIT_HW);
                }
            }
        }
        thread_active = false;
    }
    
    public override void Deactivate()
    {
        base.Deactivate();
        int counter = 10;
        while(thread_active && counter > 0){
            Thread.Sleep(50);
            counter--;
        }
        // TODO! WTF???
        if (worker != null)
            worker.Abort();
        worker = null;
    }
    
    
    
}

}


