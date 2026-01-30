namespace DMMusic.Lib.NVorbis.Contracts.Ogg
{
    interface IForwardOnlyPacketProvider : IPacketProvider
    {
        bool AddPage(byte[] buf, bool isResync);
        void SetEndOfStream();
    }
}
