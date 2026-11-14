using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// En <c>ConfiguracionDeContexto</c>, <c>TenantRlsConnectionInterceptor</c> va
/// delante de <c>TenantSelladoInterceptor</c>. Con la conexión retenida por la
/// operación de <c>PuertaAccesoDatos</c>, un <c>SaveChanges</c> empieza con la
/// conexión ya abierta: el interceptor RLS realinea primero el contexto firmado
/// con la sesión del instante y después el sellado decide, con ese contexto de
/// base, si el lote necesita el Tenant propietario de una fila de Identity. Lo
/// mismo en los ganchos de transacción: el interceptor RLS realinea antes del
/// <c>BEGIN</c> y el sellado mueve el contexto justo después, dentro del ámbito
/// transaccional del lote; tras un <c>ROLLBACK TO SAVEPOINT</c>, el RLS realinea
/// el rol y el sellado da por deshecho su movimiento. EF ejecuta los
/// interceptores en el orden de registro, así que el orden del fichero es el
/// contrato.
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
