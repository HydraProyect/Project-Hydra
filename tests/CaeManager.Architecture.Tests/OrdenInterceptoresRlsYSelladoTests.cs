using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// En <c>ConfiguracionDeContexto</c>, <c>TenantRlsConnectionInterceptor</c> va
/// delante de <c>TenantSelladoInterceptor</c>. Con la conexión retenida por la
/// operación de <c>PuertaAccesoDatos</c>, un <c>SaveChanges</c> empieza con la
/// conexión ya abierta: el interceptor RLS realinea primero el contexto firmado
/// con la sesión del instante y después el sellado lo mueve, si hace falta, al
/// Tenant propietario de una fila de Identity. Al revés, el sellado movería el
/// contexto y el primer comando del lote lo sobrescribiría con la sesión: la
/// fila de Identity se escribiría con el Tenant de sesión y no con el suyo.
/// EF ejecuta los interceptores en el orden de registro, así que el orden del
/// fichero es el contrato.
/// </summary>
public class OrdenInterceptoresRlsYSelladoTests
{
    [Fact]
    public void El_interceptor_RLS_se_registra_delante_del_sellado()
    {
        var ruta = Path.Combine(
            RaizDelRepositorio(), "src", "CaeManager.Infrastructure", "Persistence", "ConfiguracionDeContexto.cs");
        var texto = File.ReadAllText(ruta);

        var rls = texto.IndexOf("GetRequiredService<TenantRlsConnectionInterceptor>()", StringComparison.Ordinal);
        var sellado = texto.IndexOf("GetRequiredService<TenantSelladoInterceptor>()", StringComparison.Ordinal);

        rls.Should().BeGreaterThan(-1, "control: el registro del interceptor RLS tiene que encontrarse");
        sellado.Should().BeGreaterThan(-1, "control: el registro del sellado tiene que encontrarse");
        rls.Should().BeLessThan(sellado,
            "el interceptor RLS realinea el contexto al empezar el SaveChanges antes de que el sellado lo mueva");
    }

    private static string RaizDelRepositorio()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.GetFiles("*.slnx").Any() && !dir.GetFiles("*.sln").Any())
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }
}
