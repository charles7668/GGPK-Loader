using System.Threading.Tasks;

namespace GGPK_Loader.Services;

public interface IMessageService
{
    Task ShowErrorMessageAsync(string message);
}
