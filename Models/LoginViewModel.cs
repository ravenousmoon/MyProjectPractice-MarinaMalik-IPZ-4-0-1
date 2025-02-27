using System.ComponentModel.DataAnnotations;

namespace PresenceTabMalik.Models
{
    public class LoginViewModel
    {
        [Required(ErrorMessage = "Введіть логін")]
        [Display(Name = "Логін")]
        public string Username { get; set; } = null!;

        [Required(ErrorMessage = "Введіть пароль")]
        [Display(Name = "Пароль")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = null!;
    }
}