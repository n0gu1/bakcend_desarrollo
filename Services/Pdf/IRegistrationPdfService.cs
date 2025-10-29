using System.Threading;
using System.Threading.Tasks;

namespace BaseUsuarios.Api.Services.Pdf;

public interface IRegistrationPdfService
{
    Task<byte[]> BuildReceiptAsync(string nickname, string phone, string email, CancellationToken ct = default);
}
