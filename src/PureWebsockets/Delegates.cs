using System;

namespace PureWebSockets
{
    public delegate void Message(string message);

    public delegate void Data(byte[] data);

    public delegate void StateChanged(bool connected);

    public delegate void Opened();

    public delegate void Closed();

    public delegate void Error(Exception ex);

    public delegate void SendFailed(string data, Exception ex);

    public delegate void Fatality(string reason);
}