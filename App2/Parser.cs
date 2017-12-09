﻿//#define VERBOSE


using Android.OS;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Serialization;

namespace TeslaSCAN {

  [Serializable]
  public class Parser {

    CustomAdapter adapter;
    public ConcurrentDictionary<string, ListElement> items;
    SortedList<int, Packet> packets;
    public List<List<ListElement>> ignoreList;
    bool metric=true;
    long time; // if I was faster I'd use 'short time'.... :)
    int numUpdates;
    int numCells;
    public char[] tagFilter;
    private bool fastLogEnabled;
    private StreamWriter fastLogStream;
    private List<Value> fastLogItems;
    char separator = ',';
    Stopwatch logTimer;
    private MainActivity mainActivity;


    public const double miles_to_km = 1.609344;
    public const double kw_to_hp = 1.34102209;
    public const double nm_to_ftlb = 0.737562149;
    double nominalFullPackEnergy;
    double amp;
    double volt;
    double power;
    double mechPower;
    double fMechPower;
    double speed;
    double drivePowerMax;
    double torque;
    double chargeTotal;
    double dischargeTotal;
    double odometer;
    double tripDistance;
    double charge;
    double discharge;

    private double frTorque;
    private double dcChargeTotal;
    private double acChargeTotal;
    private double regenTotal;
    private double energy;
    private double regen;
    private double acCharge;
    private double dcCharge;
    private double nominalRemaining;
    private double buffer;
    private double soc;
    private double fl;
    private double fr;
    private double rl;
    private double rr;
    private int frpm;
    private int rrpm;
    private bool feet;
    private bool seat;
    private bool win;

    public Parser() {
    }

    public Parser(MainActivity mainActivity, CustomAdapter adapter) {
      this.adapter = adapter;
      this.mainActivity = mainActivity;
      items = new ConcurrentDictionary<string, ListElement>();
      packets = new SortedList<int, Packet>();
      time = SystemClock.ElapsedRealtime() + 1000;

      /* tags:
          p: performance
          t: trip
          b: battery
          c: temperature
          f: front drive unit
          s: startup (app will wait until these packets are found before starting 'normal' mode)
          i: imperial
          m: metric
          i: ignore
      */

      Packet p;

      /*packets.Add(0x256, p = new Packet(0x256, this));
      p.AddValue("Metric", "bool", "s", (bytes) => {
        metric = Convert.ToBoolean(bytes[3] & 0x80);
        if (metric) {
          foreach (var packet in packets)
            foreach (var v in packet.Value.values)
              if (v.tag.Contains("i"))
                packet.Value.values.Remove(v);
        } else {
          foreach (var packet in packets)
            foreach (var v in packet.Value.values)
              if (v.tag.Contains("m"))
                packet.Value.values.Remove(v);
        }
        return metric ? 1 : 0;
      });*/

      packets.Add(0x102, p = new Packet(0x102, this));
      p.AddValue("Battery voltage", " V", "bpr", (bytes) => volt =
          bytes.Count() >= 6 ? (bytes[0] + (bytes[1] << 8)) / 100.0 : bytes[100]); // deliberately throws outofrangeexception if bytes.Count()<=4
      p.AddValue("Battery current", " A", "b", (bytes) => amp =
          bytes.Count() < 9 ? 1000 - ((Int16)((((bytes[3] & 0x7F) << 8) + bytes[2]) << 1)) / 20.0 : bytes[100]);
      p.AddValue("Battery power", " kW", "bp", (bytes) => power = amp * volt / 1000.0);
      //p.AddValue("cell average", "Vc", "bp", (bytes) => numCells > 70 ? volt / numCells : bytes[100]);
      //p.AddValue("negative terminal", "C", (bytes) => ((bytes[6] + ((bytes[7] & 0x07) << 8))) * 0.1 - 10);

      packets.Add(0x210, p = new Packet(0x210, this));
      p.AddValue("DC-DC current", "A12", "b", (bytes) => bytes[4]);
      p.AddValue("DC-DC voltage", "V12", "b", (bytes) => bytes[5] / 10.0);
      p.AddValue("DC-DC coolant inlet", "C", "c", (bytes) => ((bytes[2] - (2 * (bytes[2] & 0x80))) * 0.5) + 40);
      p.AddValue("DC-DC input power", "W", "b", (bytes) => (bytes[3] * 16));
      p.AddValue("DC-DC output power", "W", "b", (bytes) => (bytes[4] * bytes[5] / 10.0));

      packets.Add(0x306, p = new Packet(0x306, this));
      p.AddValue("Rr inverter PCB", "C", "c", (bytes) => bytes[0] - 40);
      p.AddValue("Rr inverter", "C", "c", (bytes) => bytes[1] - 40);
      p.AddValue("Rr stator", "C", "cp", (bytes) => bytes[2] - 40);
      p.AddValue("Rr DC capacitor", "C", "c", (bytes) => bytes[3] - 40);
      p.AddValue("Rr heat sink", "C", "c", (bytes) => bytes[4] - 40);
      p.AddValue("Rr coolant inlet", "C", "c", (bytes) => bytes[5] == 0 ? bytes[100] : bytes[5] - 40);

      packets.Add(0x1D4, p = new Packet(0x1D4, this));
      p.AddValue("Fr torque measured", "Nm", "pf", (bytes) => frTorque =
         (bytes[5] + ((bytes[6] & 0x1F) << 8) - (512 * (bytes[6] & 0x10))) * 0.25);
      p.AddValue("Rr/Fr torque bias", "%", "pf", 
        (bytes) => Math.Abs(frTorque) + Math.Abs(torque) != 0 ? Math.Abs(torque) / (Math.Abs(frTorque) + Math.Abs(torque)) * 100 : bytes[100]);

      packets.Add(0x154, p = new Packet(0x154, this));
      p.AddValue("Rr torque measured", "Nm", "p", (bytes) => torque =
         (bytes[5] + ((bytes[6] & 0x1F) << 8) - (512 * (bytes[6] & 0x10))) * 0.25);
      //p.AddValue("Pedal position A", "%", "",  (bytes) => bytes[2] * 0.4);
      p.AddValue("Watt pedal", "%", "i", (bytes) => bytes[3] * 0.4);
      /*p.AddValue("HP 'measured'", "HP", "p",
          (bytes) => (torque * rpm / 9549 * kw_to_hp));*/

      packets.Add(0x2E5, p = new Packet(0x2E5, this));
      p.AddValue("Fr dissipation", "kW", "f", (bytes) => bytes[1] * 1.25);
      p.AddValue("Fr mech power", "kW", "f", (bytes) => fMechPower =
          ((bytes[2] + ((bytes[3] & 0x7) << 8)) - (512 * (bytes[3] & 0x4))) / 2.0);
      p.AddValue("Fr mech power HP", "HP", "pf", (bytes) => fMechPower * kw_to_hp);
      p.AddValue("Fr stator current", "A", "f", (bytes) => bytes[4] + ((bytes[5] & 0x7) << 8));
      p.AddValue("Fr drive power max", "kW", "bc", (bytes) => drivePowerMax =
          (((bytes[6] & 0x3F) << 5) + ((bytes[5] & 0xF0) >> 3)) + 1);
      p.AddValue("Mech power combined", "kW", "f", (bytes) => mechPower + fMechPower);
      p.AddValue("HP combined", "HP", "pf", (bytes) => (mechPower + fMechPower) * kw_to_hp);

      packets.Add(0x266, p = new Packet(0x266, this));
      p.AddValue("Rr inverter 12V", "V12", "", (bytes) => bytes[0] / 10.0);
      p.AddValue("Rr dissipation", "kW", "", (bytes) => bytes[1] * 1.25);
      p.AddValue("Rr mech power", "kW", "", (bytes) => mechPower =
          ((bytes[2] + ((bytes[3] & 0x7) << 8)) - (512 * (bytes[3] & 0x4))) / 2.0);
      p.AddValue("Rr mech power HP", "HP", "p", (bytes) => mechPower * kw_to_hp);
      p.AddValue("Rr stator current", "A", "", (bytes) => bytes[4] + ((bytes[5] & 0x7) << 8));
      p.AddValue("Rr regen power max", "kW", "b", (bytes) => (bytes[7] * 4) - 200);
      p.AddValue("Rr drive power max", "kW", "b", (bytes) => drivePowerMax =
          (((bytes[6] & 0x3F) << 5) + ((bytes[5] & 0xF0) >> 3)) + 1);

      packets.Add(0x145, p = new Packet(0x145, this));
      p.AddValue("Fr torque estimate", "Nm", "f",
          (bytes) => ((bytes[0] + ((bytes[1] & 0xF) << 8)) - (512 * (bytes[1] & 0x8))) / 2);

      packets.Add(0x116, p = new Packet(0x116, this));
        p.AddValue("Rr torque estimate", "Nm", "",
            (bytes) => ((bytes[0] + ((bytes[1] & 0xF) << 8)) - (512 * (bytes[1] & 0x8))) / 2);
        p.AddValue("Speed", "km|h", "",
            (bytes) => speed = ((bytes[2] + ((bytes[3] & 0xF) << 8)) - 500) / 20.0 * miles_to_km);
        p.AddValue("Consumption", "wh|km", "p",
            (bytes) => power / speed * 1000,
            new int[] { 0x102 });

      packets.Add(0x382, p = new Packet(0x382, this));
      p.AddValue("Nominal full pack", "kWh", "br", (bytes) => nominalFullPackEnergy = (bytes[0] + ((bytes[1] & 0x03) << 8)) * 0.1);
      p.AddValue("Nominal remaining", "kWh", "br", (bytes) => nominalRemaining = ((bytes[1] >> 2) + ((bytes[2] & 0x0F) * 64)) * 0.1);
      p.AddValue("Expected remaining", "kWh", "r", (bytes) => ((bytes[2] >> 4) + ((bytes[3] & 0x3F) * 16)) * 0.1);
      p.AddValue("Ideal remaining", "kWh", "r", (bytes) => ((bytes[3] >> 6) + ((bytes[4] & 0xFF) * 4)) * 0.1);
      p.AddValue("To charge complete", "kWh", "", (bytes) => (bytes[5] + ((bytes[6] & 0x03) << 8)) * 0.1);
      p.AddValue("Energy buffer", "kWh", "br", (bytes) => buffer = ((bytes[6] >> 2) + ((bytes[7] & 0x03) * 64)) * 0.1);
      //p.AddValue("SOC nominal", "%", "br", (bytes) => nominalRemaining / nominalFullPackEnergy * 100.0);
      p.AddValue("SOC", "%", "br", (bytes) => soc = (nominalRemaining - buffer) / (nominalFullPackEnergy - buffer) * 100.0);
      p.AddValue("Usable full pack", "kWh", "br", (bytes) => (nominalFullPackEnergy-buffer));
      p.AddValue("Usable remaining", "kWh", "br", (bytes) => (nominalRemaining-buffer));

      packets.Add(0x302, p = new Packet(0x302, this));
      //p.AddValue("SOC Min", "%", "br", (bytes) => (bytes[0] + ((bytes[1] & 0x3) << 8)) / 10.0);
      //p.AddValue("SOC UI", "%", "br", (bytes) => ((bytes[1] >> 2) + ((bytes[2] & 0xF) << 6)) / 10.0);

      p.AddValue("DC Charge total", "kWH", "bs",
            (bytes) => {
              if (bytes[2] >> 4 == 0) {
                dcChargeTotal =
                  (bytes[4] +
                  (bytes[5] << 8) +
                  (bytes[6] << 16) +
                  (bytes[7] << 24)) / 1000.0;
                if (mainActivity.currentTab.trip.dcChargeStart == 0)
                  mainActivity.currentTab.trip.dcChargeStart = dcChargeTotal;
                dcCharge = dcChargeTotal - mainActivity.currentTab.trip.dcChargeStart;
                return dcChargeTotal;
              } else return bytes[100];
            });

      p.AddValue("AC Charge total", "kWH", "bs",
        (bytes) => {
          if (bytes[2] >> 4 == 1) {
            acChargeTotal =
              (bytes[4] +
              (bytes[5] << 8) +
              (bytes[6] << 16) +
              (bytes[7] << 24)) / 1000.0;
            if (mainActivity.currentTab.trip.acChargeStart == 0)
              mainActivity.currentTab.trip.acChargeStart = acChargeTotal;
            acCharge = acChargeTotal - mainActivity.currentTab.trip.acChargeStart;
            return acChargeTotal;
          } else return bytes[100];
        });
      p.AddValue("DC Charge", "kWh", "ti",
        (bytes) => dcChargeTotal - mainActivity.currentTab.trip.dcChargeStart);
      p.AddValue("AC Charge", "kWh", "ti",
        (bytes) => acChargeTotal - mainActivity.currentTab.trip.acChargeStart);

      packets.Add(0x3D2, p = new Packet(0x3D2, this));
      p.AddValue("Charge total", "kWH", "bs",
                (bytes) => {
                  chargeTotal =
                    (bytes[0] +
                    (bytes[1] << 8) +
                    (bytes[2] << 16) +
                    (bytes[3] << 24)) / 1000.0;
                  if (mainActivity.currentTab.trip.chargeStart == 0)
                    mainActivity.currentTab.trip.chargeStart = chargeTotal;
                  charge = chargeTotal - mainActivity.currentTab.trip.chargeStart;
                  return chargeTotal;
                });

      p.AddValue("Discharge total", "kWH", "b",
          (bytes) => {
            dischargeTotal =
              (bytes[4] +
              (bytes[5] << 8) +
              (bytes[6] << 16) +
              (bytes[7] << 24)) / 1000.0;
            if (mainActivity.currentTab.trip.dischargeStart == 0)
              mainActivity.currentTab.trip.dischargeStart = dischargeTotal;
            discharge = dischargeTotal - mainActivity.currentTab.trip.dischargeStart;
            return dischargeTotal;
          });
      p.AddValue("Regenerated", "kWh", "tr",
          (bytes) => regen = charge - acCharge - dcCharge);
      p.AddValue("Energy", "kWh", "tr",
          (bytes) => energy = discharge - regen);
      p.AddValue("Discharge", "kWh", "r",
          (bytes) => discharge);
      p.AddValue("Charge", "kWh", "r",
          (bytes) => charge);
      p.AddValue("Regen total", "kWH", "b",
        (bytes) => regenTotal = chargeTotal - acChargeTotal - dcChargeTotal,
        new int[] { 0x302 });
      p.AddValue("Regen %", "% ", "tr",
          (bytes) => energy > 0 ? regen / discharge * 100 : bytes[100]);//,
                  //new int[] { 0x302 });

      p.AddValue("Discharge cycles", "x", "b",
          (bytes) => nominalFullPackEnergy>0 ? dischargeTotal / nominalFullPackEnergy : bytes[100], 
          new int[] { 0x382 });
      p.AddValue("Charge cycles", "x", "b",
          (bytes) => nominalFullPackEnergy>0 ? chargeTotal / nominalFullPackEnergy : bytes[100], 
          new int[] { 0x382 });

      packets.Add(0x562, p = new Packet(0x562, this));
      p.AddValue("Battery odometer", "Km", "b",
          (bytes) => odometer = (bytes[0] + (bytes[1] << 8) + (bytes[2] << 16) + (bytes[3] << 24)) / 1000.0 * miles_to_km);
      p.AddValue("Trip distance", "km", "tsr",
          (bytes) => {
            if (mainActivity.currentTab.trip.odometerStart == 0)
              mainActivity.currentTab.trip.odometerStart = odometer;
            return tripDistance = odometer - mainActivity.currentTab.trip.odometerStart;
          });
      p.AddValue("Trip consumption", "wh|km", "tr",
          (bytes) => tripDistance > 0 ? energy / tripDistance * 1000 : bytes[100],
          new int[] { 0x3D2 });
      /*p.AddValue("Lifetime consumption", "wh/km", "bt",
          (bytes) => odometer > 0 ? dischargeTotal / odometer * 1000 : bytes[100]);*/

      packets.Add(0x115, p = new Packet(0x115, this));
      p.AddValue("Fr motor RPM", "RPM", "",
          (bytes) => frpm = (bytes[4] + (bytes[5] << 8)) - (512 * (bytes[5] & 0x80)));
      // 0x115 --- DIS_motorRPM = (data[4] + (data[5]<<8)) - (512 * (data[5]&0x80));

      packets.Add(0x106, p = new Packet(0x106, this));
      p.AddValue("Rr motor RPM", "RPM", "",
          (bytes) => rrpm = (bytes[4] + (bytes[5] << 8)) - (512 * (bytes[5] & 0x80)));

      packets.Add(0x232, p = new Packet(0x232, this));
      p.AddValue("Max discharge power", "kW", "b", (bytes) => (bytes[2] + (bytes[3] << 8)) / 100.0);
      p.AddValue("Max regen power", "kW", "b", (bytes) => (bytes[0] + (bytes[1] << 8)) / 100.0);

      packets.Add(0x168, p = new Packet(0x168, this));
      p.AddValue("Brake pedal", "%", "i",
          (bytes) => (bytes[0] + (bytes[1] << 8)) - 3239);

      packets.Add(0x00E, p = new Packet(0x00E, this));
      p.AddValue("Steering angle", "deg", "i",
        (bytes) => (((bytes[0] << 8) + bytes[1] - 8200.0) / 10.0));

      packets.Add(0x338, p = new Packet(0x338, this));
      p.AddValue("Rated range", "km", "br",
        (bytes) => (bytes[0] + (bytes[1] << 8)) * miles_to_km);
      p.AddValue("Typical range", "km", "br",
        (bytes) => (bytes[2] + (bytes[3] << 8)) * miles_to_km);
      p.AddValue("Full rated range", "km", "br",
        (bytes) => (bytes[0] + (bytes[1] << 8)) * miles_to_km / (soc == 0.0 ? 100.0 : soc) * 100.0);
      p.AddValue("Full typical range", "km", "br",
        (bytes) => (bytes[2] + (bytes[3] << 8)) * miles_to_km / (soc == 0.0 ? 100.0 : soc) * 100.0);



      packets.Add(0x6F2, p = new Packet(0x6F2, this));
      p.AddValue("Last cell block updated", "xb", "", (bytes) => {
        Int64 data = BitConverter.ToInt64(bytes, 0);
        if (bytes[0] < 24) {
          int cell = 0;
          for (int i = 0; i < 4; i++)
            UpdateItem("Cell " + (cell = ((bytes[0]) * 4 + i + 1)).ToString().PadLeft(2) + " voltage"
              , "zVC"
              , "z"
              , bytes[0]
              , ((data >> ((14 * i) + 8)) & 0x3FFF) * 0.000305
              , 0x6F2);
          if (cell > numCells)
            numCells = cell;
        } else
          for (int i = 0; i < 4; i++)
            UpdateItem("Cell " + ((bytes[0] - 24) * 4 + i + 1).ToString().PadLeft(2) + " temp"
              , "zCC"
              , "c"
              , bytes[0]
              , ((Int16)(((data >> ((14 * i) + 6)) & 0xFFFC)) * 0.0122 / 4.0)
              , 0x6F2);

        return bytes[0];
      });

      // these are a bit stupid, but they are placeholders for the filters to be generated correctly.
      p.AddValue("Cell temp min", "C", "b", null);
      p.AddValue("Cell temp avg", "C", "bcp", null);
      p.AddValue("Cell temp max", "C", "b", null);
      p.AddValue("Cell temp diff", "Cd", "bc", null);
      p.AddValue("Cell min", "Vc", "b", null);
      p.AddValue("Cell avg", "Vc", "bpzr", null);
      p.AddValue("Cell max", "Vc", "b", null);
      p.AddValue("Cell diff", "Vcd", "bz", null);
      for (int i = 1; i <= 96; i++)
        p.AddValue("Cell " + i.ToString().PadLeft(2) + " voltage"
          , "zVC"
          , "z", null);
      for (int i = 1; i <= 32; i++)
        p.AddValue("Cell " + i.ToString().PadLeft(2) + " temp"
          , "zCC"
          , "c"
          , null);


      /*packets.Add(0x222, p = new Packet(0x222, this));
      p.AddValue("Charge rate", "??", "e",
        (bytes) => (bytes[0] + (bytes[1] << 8)) / 100.0);
      p.AddValue("Charger volt", "V", "e",
        (bytes) => (bytes[2] + (bytes[3] << 8)) / 100.0);*/


      packets.Add(0x2A8, p = new Packet(0x2A8, this));
      p.AddValue("Front left", "WRPM", "p",
        (bytes) => fl = (bytes[4] + (bytes[3] << 8)) * 0.7371875 / 9.73);
      p.AddValue("Front right", "WRPM", "p",
        (bytes) => fr = (bytes[6] + (bytes[5] << 8)) * 0.7371875 / 9.73);
      p.AddValue("Front drive ratio", ":1", "",
        (bytes) => frpm > 1000 ? frpm / ((fl + fr) / 2) : bytes[100],
        new int[] { 0x115 });


      packets.Add(0x288, p = new Packet(0x288, this));
      p.AddValue("Rear left", "WRPM", "p",
        (bytes) => rl = (bytes[4] + (bytes[3] << 8)) * 0.7371875 / 9.73);
      p.AddValue("Rear right", "WRPM", "p",
        (bytes) => rr = (bytes[7] + (bytes[6] << 8)) * 0.7371875 / 9.73);
      p.AddValue("Rear drive ratio", ":1", "",
        (bytes) => rrpm > 1000 ? rrpm / ((rl + rr) / 2) : bytes[100],
        new int[] { 0x106 });

      packets.Add(0x318, p = new Packet(0x318, this));
      p.AddValue("Outside temp", " C", "h",
        (bytes) => (bytes[0] / 2.0 - 40));
      p.AddValue("Outside temp filtered", " C", "h",
        (bytes) => (bytes[1] / 2.0 - 40));
      p.AddValue("Inside temp", " C", "h",
        (bytes) => (bytes[2] / 2.0 - 40));
      p.AddValue("A/C air temp", " C", "h",
        (bytes) => (bytes[4] / 2.0 - 40));
      //318 - temperaturer. 0, 1, 2, 4:  / 2 - 40 = C


      packets.Add(0x2AA, p = new Packet(0x2AA, this));
      p.AddValue("HVAC floor", "0", "h",
          (bytes) => {
            var set1 = bytes[2] & 0x07;
            feet = false;
            seat = false;
            win = false;
            switch (set1) {
              case 1:
                seat = true;
                break;
              case 2:
                feet = true;
                seat = true;
                break;
              case 3:
                feet = true;
                break;
              case 4:
                feet = true;
                win = true;
                break;
              case 5:
                win = true;
                break;
              case 6:
                feet = true;
                seat = true;
                win = true;
                break;
              case 7:
                seat = true;
                win = true;
                break;
            }
            return feet ? 1 : 0;
          });
      p.AddValue("HVAC mid", "0", "h",
          (bytes) => seat ? 1 : 0);
      p.AddValue("HVAC window", "0", "h",
          (bytes) => win ? 1 : 0);

      /*p.AddValue("HVAC recycle", "0", "eh",
          (bytes) => {
            return (bytes[3] & 0x10) >> 4;
          });
      p.AddValue("HVAC recycle2", "0", "eh",
          (bytes) => {
            return (bytes[3] & 0x8) >> 3;
          });*/
      p.AddValue("HVAC A/C", "0", "h",
          (bytes) => {
            var set3 = bytes[4] & 0x01;
            return set3;
          });
      p.AddValue("HVAC on/off", "0", "h",
          (bytes) =>
             (bytes[3] & 0x10) >> 4 == 0 ? 1 : 0);

      p.AddValue("HVAC fan speed", "X", "h",
          (bytes) => (bytes[2] & 0xf0) >> 4);

      p.AddValue("HVAC temp left", " C", "h",
          (bytes) => bytes[0] / 2.0);
      p.AddValue("HVAC temp right", " C", "h",
          (bytes) => bytes[1] / 2.0);



      packets.Add(0x3F8, p = new Packet(0x3F8, this));
      p.AddValue("Win L", " C", "h",
        (bytes) => ((bytes[4] + (bytes[5] << 8)) / 40.0));
      p.AddValue("Win R", " C", "h",
        (bytes) => ((bytes[6] + (bytes[7] << 8)) / 40.0));
      p.AddValue("Mid L", " C", "h",
        (bytes) => ((bytes[0] + (bytes[1] << 8)) / 40.0));
      p.AddValue("Mid R", " C", "h",
        (bytes) => ((bytes[2] + (bytes[3] << 8)) / 40.0));
      //3F8 - as int. tror dette er 4 tempavlesninger evt innblåstemperatur, F / 10->C

      packets.Add(0x388, p = new Packet(0x388, this));
      p.AddValue("Floor L", " C", "h",
        (bytes) => (bytes[1] / 4.0));
      p.AddValue("Floor R", " C", "h",
        (bytes) => (bytes[0] / 4.0));
      p.AddValue("Temp 1", " C", "h",
        (bytes) => (bytes[2] / 4.0));
      p.AddValue("Temp 2", " C", "h",
        (bytes) => (bytes[3] / 4.0));
      p.AddValue("Temp 3", " C", "h",
        (bytes) => (bytes[4] / 4.0));
      p.AddValue("Temp 4", " C", "h",
        (bytes) => (bytes[5] / 4.0));
      //388 - temperaturer!0 - 1: / 4 = C, 2,3,4,5: / 2 - 40 = C

      packets.Add(0x308, p = new Packet(0x308, this));
      p.AddValue("Louver 1", "b", "h",
        (bytes) => (bytes[0]));
      p.AddValue("Louver 2", "b", "h",
        (bytes) => (bytes[1]));
      p.AddValue("Louver 3", "b", "h",
        (bytes) => (bytes[2]));
      p.AddValue("Louver 4", "b", "h",
        (bytes) => (bytes[3]));
      p.AddValue("Louver 5", "b", "h",
        (bytes) => (bytes[4]));
      p.AddValue("Louver 6", "b", "h",
        (bytes) => (bytes[5]));
      p.AddValue("Louver 7", "b", "h",
        (bytes) => (bytes[6]));
      p.AddValue("Louver 8", "b", "h",
        (bytes) => (bytes[7]));
      //388 - temperaturer!0 - 1: / 4 = C, 2,3,4,5: / 2 - 40 = C




    }



    public List<Value> GetAllValues() {
      List<Value> result = new List<TeslaSCAN.Value>();
      foreach (var p in packets)
        foreach (var v in p.Value.values)
          result.Add(v);
      return result;
    }

    internal void LogFast(bool logfast, string path) {
      if (logfast) {
        //string path = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal);
        string filename = Path.Combine(path, mainActivity.currentTab.name + " " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".csv");
        fastLogStream = new StreamWriter(filename, true);
        fastLogItems = mainActivity.currentTab.include;
/*#if disablebluetooth
        fastLogItems.Insert(0, new Value("Raw", "", "", null, null));
#endif*/
      string s = "Time,";
        foreach (var i in fastLogItems
         /* .OrderBy(x => x.index)
          .OrderBy(x => x.packetId)*/) {
            s += i.name + separator;
        }
        fastLogStream.WriteLine(s);
        fastLogEnabled = true;
        logTimer = new Stopwatch();
        logTimer.Start();
      }
      else {
        fastLogStream?.Close();
        fastLogEnabled = false;
      }
    }

    public void SaveTrip(string path) {
      string filename = Path.Combine(path, "TripStart " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".xml");
      FileStream output = new FileStream(filename, FileMode.Create);
      XmlSerializer x = new XmlSerializer(typeof(Trip));
      x.Serialize(output, mainActivity.currentTab.trip);
      output.Close();
    }

    public void LoadTrip(string fileName) {
      XmlSerializer mySerializer = new XmlSerializer(typeof(Trip));
      FileStream myFileStream = new FileStream(fileName, FileMode.Open);
      // Call the Deserialize method and cast to the object type.
      mainActivity.currentTab.trip = (Trip)mySerializer.Deserialize(myFileStream);
      myFileStream.Close();
    }

    public void ResetTrip() {
      mainActivity.currentTab.trip = new Trip(false);
    }

    private void ParsePacket(string raw, int id, byte[] bytes) {
      if (packets.ContainsKey(id)) {
        packets[id].Update(bytes);
        numUpdates++;
        if (id == 0x6F2)
          if (bytes[0] >= 24) {
            var values = items.Where(x => x.Value.unit == "zCC");
            double min = values.Min(x => x.Value.GetValue(false));
            double max = values.Max(x => x.Value.GetValue(false));
            double avg = values.Average(x => x.Value.GetValue(false));
            UpdateItem("Cell temp min", "c", "bcz", 0, min, 0x6F2);
            UpdateItem("Cell temp avg", "c", "bcpz", 1, avg, 0x6F2);
            UpdateItem("Cell temp max", "c", "bcz", 2, max, 0x6F2);
            UpdateItem("Cell temp diff", "Cd", "bcz", 3, max - min, 0x6F2);
          } else {
            var values = items.Where(x => x.Value.unit == "zVC");
            double min = values.Min(x => x.Value.GetValue(false));
            double max = values.Max(x => x.Value.GetValue(false));
            double avg = values.Average(x => x.Value.GetValue(false));
            UpdateItem("Cell min", "Vc", "bz", 0, min, 0x6F2);
            UpdateItem("Cell avg", "Vc", "bpz", 1, avg, 0x6F2);
            UpdateItem("Cell max", "Vc", "bz", 2, max, 0x6F2);
            UpdateItem("Cell diff", "Vcd", "bz", 3, max - min, 0x6F2);
          }
        if (time < SystemClock.ElapsedRealtime()) {
          UpdateItem("x  Packets per second", "xp", "", 0, numUpdates, 0xFFF);
          numUpdates = 0;
          time = SystemClock.ElapsedRealtime() + 1000;
          foreach (var item in items.Where(x => x.Value.LimitsChanged()).Select(x => x.Value))
            adapter.Touch(item);
        }
        if (fastLogEnabled) {
          string s = "";
          int pos = 0, lastPos = 0;
          bool anythingToLog = false;
          foreach (var logItem in fastLogItems) {
            pos++;
            /*if (logItem.name == "Raw")
              s += raw;*/
            if (logItem.packetId!=null && logItem.packetId.Contains(id)) {
              lastPos = pos;
              if (items.ContainsKey(logItem.name)) { // in the case of Front RPM, the next line throws exception on an RWD car (because front RPM is not in items)
                s += items[logItem.name].GetValue(((MainActivity)adapter.GetContext()).convertToImperial)
                  .ToString(System.Globalization.CultureInfo.InvariantCulture);
                anythingToLog = true;
              }
            }
            s += separator;
          }
          if (anythingToLog) 
            fastLogStream.WriteLine(logTimer.ElapsedMilliseconds.ToString() + separator + s);          
        }
      }
    }

    public void UpdateItem(string name, string unit, string tag, int index, double value, int id) {
      ListElement l;
      items.TryGetValue(name, out l);
      if (l == null) {
        items.TryAdd(name, l = new ListElement(name, unit, tag, index, value, adapter, id));
        mainActivity.currentTab.AddElements(l);
        adapter.GetContext().RunOnUiThread(() => {
          adapter.items = mainActivity.currentTab.GetItems(this);
          adapter.NotifyChange();
        });
      } else l.SetValue(value);
      if (l.changed)
        adapter.Touch(l);
    }


    public List<ListElement> GetDefaultItems() {
      return items
        .Values
        .OrderBy(x => x.index)
        .OrderBy(x => { x.selected = false; return x.unit; })
        .ToList<ListElement>();
    }

    public List<ListElement> GetItems(string tag) {
      if (tag=="" || tag==null)
        return GetDefaultItems();
      var charArray = tag.ToCharArray(); // I'll cache it to be nice to the CPU cycles
      tagFilter = charArray;
      return items
        .OrderBy(x => x.Value.index)
        .OrderBy(x => x.Value.unit)
        .Where(x => x.Value.tag?.IndexOfAny(charArray) >= 0)
        .Select(x => { x.Value.selected = false; return x.Value; })
        .ToList();
    }

    public List<Value> GetValues(string tag) {
      var charArray = tag.ToCharArray(); // I'll cache it to be nice to the CPU cycles
      tagFilter = charArray;
      List<Value> values = new List<Value>();
      foreach (var packet in packets)
        foreach (var value in packet.Value.values)
          if (value.tag.IndexOfAny(charArray) >= 0 || tag=="")
            values.Add(value);

      return values
        //.OrderBy(x => x.index)
        //.OrderBy(x => x.unit)
        .ToList();
    }


    public string[] GetCANFilter(List<Value> items) {
      var f=items.FirstOrDefault();
      int filter=0;
      if (f != null)
        filter = f.packetId.First();
      int mask = 0;

      List<int> ids = new List<int>();
      foreach (var item in items)
        foreach (var id in item.packetId)
          if (!ids.Exists(x => x == id))
            ids.Add(id);

      foreach (var id in ids) {
        for (int bit = 0; bit < 11; bit++)
          if (((id >> bit) & 1) != ((filter >> bit) & 1)) {
            mask |= 1 << bit;
            //filter &= ~(1 << bit);
          }
      }
      mask = ~mask & 0x7FF;
      Console.WriteLine(Convert.ToString(mask, 2).PadLeft(11, '0'));
      Console.WriteLine("{0,4} filter: {1,3:X} mask: {2,3:X}", 1, filter, mask, 1, 1);
      List<string> result = new List<string>();
      result.Add(Convert.ToString(mask, 16));
      result.Add(Convert.ToString(filter, 16));
      foreach (int id in ids)
        result.Add(Convert.ToString(id, 16));
      return result.ToArray();
    }

    public string[] GetCANFilter(string tag) {
      int filter = 0;
      int mask = 0;
      List<int> ids = new List<int>();
      foreach (var packet in packets.Values)
        foreach (var value in packet
          .values
          .Where(x => x.tag.IndexOfAny(tag.ToCharArray()) >= 0 || tag==""))
          if (!ids.Exists(x=>x == packet.id))
          ids.Add(packet.id);

      if (tag.Contains('z'))
        ids.Add(0x6F2);     

      foreach (var id in ids) {
        if (filter == 0)
          filter = id;
        for (int bit = 0; bit < 11; bit++)
          if (((id >> bit) & 1) != ((filter >> bit) & 1)) {
            mask |= 1 << bit;
            //filter &= ~(1 << bit);
          }
      }
      mask = ~mask & 0x7FF;
      Console.WriteLine(Convert.ToString(mask, 2).PadLeft(11, '0'));
      Console.WriteLine("{0,4} filter: {1,3:X} mask: {2,3:X}", 1, filter, mask, 1, 1);
      List<string> result = new List<string>();
      result.Add(Convert.ToString(mask, 16));
      result.Add(Convert.ToString(filter, 16));
      foreach (int id in ids)
        result.Add(Convert.ToString(id, 16));
      return result.ToArray();
    }

    // returns true IF startup=true AND all packets tagged with 's' have been received.

    public List<int> GetCANids(string tag) {
      int filter = 0;
      int mask = 0;
      List<int> ids = new List<int>();
      foreach (var packet in packets.Values)
        foreach (var value in packet
          .values
          .Where(x => x.tag.IndexOfAny(tag.ToCharArray()) >= 0 || tag == ""))
          if (!ids.Exists(x => x == packet.id))
            ids.Add(packet.id);
      return ids;
    }


    public bool Parse(string input, int idToFind) {
      if (!input.Contains('\n'))
        return false;
      if (input.StartsWith(">"))
        input = input.Substring(1);
      List<string> lines = input?.Split('\n').ToList();
      lines.Remove(lines.Last());

      bool found = false;

      foreach (var line in lines)
        try {
          if (!(line.Length == 11 && line.StartsWith("562")) &&
              !(line.Length == 15 && line.StartsWith("116")) &&
              !(line.Length == 17 && (line.StartsWith("210")||line.StartsWith("115"))) &&
               line.Length != 19) { // testing an aggressive garbage collector! // 11)
#if VERBOSE
            Console.WriteLine("GC " + line);
#endif
            continue;
          }
#if VERBOSE
          Console.WriteLine(line);
#endif
          int id = 0;
          if (!int.TryParse(line.Substring(0,3), System.Globalization.NumberStyles.HexNumber, null, out id))
            continue;
          string[] raw = new string[(line.Length - 3) / 2];
          int r = 0;
          int i;
          for (i = 3; i < line.Length-1; i += 2)
            raw[r++] = line.Substring(i,2);
          List<byte> bytes = new List<byte>();
          i = 0;
          byte b = 0;
          for (i = 0; i < raw.Length; i++)
            if (raw[i].Length != 2 || !byte.TryParse(raw[i], System.Globalization.NumberStyles.HexNumber, null, out b))
              break;
            else bytes.Add(b);
#if disablebluetooth
          if (fastLogEnabled)
            fastLogStream.WriteLine(line);
#endif
          if (bytes.Count == raw.Length) { // try to validate the parsing 
            ParsePacket(line, id, bytes.ToArray());
            MainActivity.bluetoothHandler.ResetTimeout();
            if (idToFind>0)
              if (idToFind == id)
                found=true;
          }
        } catch (Exception e) { Console.WriteLine(e.ToString()); };

      /*if (startup) {
        bool foundAll = true;
        foreach (var p in packets)
          foreach (var v in p.Value.values)
            if (v.tag.Contains('s') &&
            !items.ContainsKey(v.name)) {
              foundAll = false;
              break;
            }
        return foundAll;
      }*/
      if (found) return true;
      return false;
    }


  }
}

