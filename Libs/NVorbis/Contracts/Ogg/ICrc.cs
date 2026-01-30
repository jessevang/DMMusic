namespace DMMusic.Lib.NVorbis.Contracts.Ogg
{
    interface ICrc
    {
        void Reset();
        void Update(int nextVal);
        bool Test(uint checkCrc);
    }
}
