using System.Net.NetworkInformation;
using WinPulse.Core.Model;
using WinPulse.Core;

namespace WinPulse.Collectors;

/// <summary>Скорость приёма и передачи по каждому подключённому адаптеру (разница счётчиков за секунду).</summary>
internal sealed class NetworkCollector
{
    private readonly Dictionary<string, (long Rx, long Tx, DateTime Time)> _prev = new(StringComparer.OrdinalIgnoreCase);

    public List<NetworkAdapterState> Read()
    {
        var now = DateTime.UtcNow;
        var result = new List<NetworkAdapterState>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            long rx, tx, speed;
            try
            {
                var s = ni.GetIPStatistics();
                rx = s.BytesReceived;
                tx = s.BytesSent;
                speed = Math.Max(0, ni.Speed);
            }
            catch
            {
                continue;
            }

            double rxBps = 0, txBps = 0;
            if (_prev.TryGetValue(ni.Id, out var prev))
            {
                double seconds = (now - prev.Time).TotalSeconds;
                if (seconds > 0.05)
                {
                    rxBps = Math.Max(0, rx - prev.Rx) / seconds;
                    txBps = Math.Max(0, tx - prev.Tx) / seconds;
                }
            }
            _prev[ni.Id] = (rx, tx, now);

            result.Add(new NetworkAdapterState
            {
                Id = ni.Id,
                Name = ni.Name,
                Description = ni.Description,
                Kind = ni.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Ppp => "PPP",
                    NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => Lang.T("Мобильная сеть", "Mobile network"),
                    _ => ni.NetworkInterfaceType.ToString(),
                },
                SpeedBitsPerSec = speed,
                ReceiveBps = rxBps,
                SendBps = txBps,
            });
        }
        return result;
    }
}
