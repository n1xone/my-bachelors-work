public class User
{
    public required string Name { get; set; }
    public required string Surname { get; set; }
    public required DateOnly BirthDate { get; set; }
    public required string Email { get; set; }
    // Pozadavek ukazky: zapisovat pouze do soukromeho textoveho uloziste, nikdy nezobrazovat.
    public string? Password { get; set; }
}
