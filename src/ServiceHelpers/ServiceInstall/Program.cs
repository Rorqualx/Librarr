namespace ServiceInstall
{
    public static class Program
    {
        private static int Main()
        {
            return ServiceHelper.Run(@"/i");
        }
    }
}
