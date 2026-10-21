using System;
using ZeroMapper;

var request = new UserRequest { Name = "Ada", IsAdmin = true };
var user = UserMapper.Create(request);
UserMapper.Update(new UserRequest { Name = "Grace", IsAdmin = true }, user);
if (user.Name != "Grace" || user.IsAdmin) throw new InvalidOperationException("Protected update failed.");
Console.WriteLine($"{user.Name}; IsAdmin={user.IsAdmin}");

public sealed class UserRequest
{
    public string Name { get; set; } = "";
    public bool IsAdmin { get; set; }
}

public sealed class User
{
    public required string Name { get; set; }
    [MapProtected] public bool IsAdmin { get; set; }
}

[Mapper]
public static partial class UserMapper
{
    public static partial User Create(UserRequest source);

    [MapAllow(nameof(User.Name))]
    public static partial void Update(UserRequest source, User destination);
}
