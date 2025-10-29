using BaseUsuarios.Api.Models;

namespace BaseUsuarios.Api.Repositories;

public interface IUserRepository
{
    Task<long> CreateAsync(UserCreateDto dto);
    Task<UserDto?> GetByIdAsync(long id);
    Task<IReadOnlyList<UserDto>> ListAsync();
    Task<UserLoginResult> LoginAsync(string credential, string password);
    Task<UserPhotoDto?> GetPhotoAsync(long id);
    Task<bool> UpdatePhotoAsync(UserPhotoDto dto);
}