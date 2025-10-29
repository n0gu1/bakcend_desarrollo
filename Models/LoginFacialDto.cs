namespace BaseUsuarios.Api.Models
{
    public sealed class LoginFacialDto
    {
        public string PhotoBase64 { get; set; } = "";
        public int MaxCandidates { get; set; } = 200;
        public int DegreeOfParallelism { get; set; } = 8;
        public double MinPercent { get; set; } = 60;
    }
}
