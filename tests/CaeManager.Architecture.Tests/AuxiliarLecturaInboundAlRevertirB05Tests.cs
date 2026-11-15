using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// El Down de <c>AnadeEstadoContratoAcceso</c> (B-05) devuelve la auxiliar <c>app_inbound_tenant_contratista_del_contrato</c>
/// de la lectura entre Tenants al cuerpo que dejó <c>AnadeLineaInboundProductoContratado</c> (B-01), con el gate de la
/// línea Inbound. Si alguien cambiara la auxiliar en B-01 o en el Down, revertir B-05 dejaría otra función distinta de la
/// que había, quizá más amplia, sin que ningún test lo viera: la integración solo comprueba el Up.
/// </summary>
public class AuxiliarLecturaInboundAlRevertirB05Tests
{
    private const string MigracionB01 = "20261114193929_AnadeLineaInboundProductoContratado";
    private const string MigracionB05 = "20261115061922_AnadeEstadoContratoAcceso";

    [Fact]
    public void El_Down_de_B05_restaura_la_auxiliar_exacta_del_Up_de_B01()
    {
        var b01 = File.ReadAllText(Path.Combine(DirectorioMigraciones(), MigracionB01 + ".cs"));
        var b05 = File.ReadAllText(Path.Combine(DirectorioMigraciones(), MigracionB05 + ".cs"));

        var upB01 = Auxiliar(Up(b01));
        var upB05 = Auxiliar(Up(b05));
        var downB05 = Auxiliar(Down(b05));

        upB05.Should().Contain("c.\"Estado\" IN ('Activo', 'Finalizado')",
            "control positivo: el extractor encuentra la auxiliar de B-05, que filtra por estado");
        upB05.Should().NotBe(upB01, "control positivo: el Up de B-05 sí cambia la auxiliar");
        downB05.Should().Be(upB01);
    }

    /// <summary>El único <c>CREATE OR REPLACE FUNCTION</c> de la auxiliar en el tramo, hasta su <c>$$;</c>, sin espacios de más.</summary>
    private static string Auxiliar(string tramo)
    {
        var coincidencias = Regex.Matches(
            tramo,
            @"CREATE OR REPLACE FUNCTION public\.app_inbound_tenant_contratista_del_contrato\(.*?\$\$;",
            RegexOptions.Singleline);
        coincidencias.Should().ContainSingle("el tramo reescribe la auxiliar una sola vez");
        return Regex.Replace(coincidencias[0].Value, @"\s+", " ").Trim();
    }

    private static string Up(string texto)
    {
        var inicio = texto.IndexOf("void Up(", StringComparison.Ordinal);
        var fin = texto.IndexOf("void Down(", StringComparison.Ordinal);
        inicio.Should().BeGreaterThan(0);
        fin.Should().BeGreaterThan(inicio);
        return texto[inicio..fin];
    }

    private static string Down(string texto)
    {
        var inicio = texto.IndexOf("void Down(", StringComparison.Ordinal);
        inicio.Should().BeGreaterThan(0);
        return texto[inicio..];
    }

    private static string DirectorioMigraciones() =>
        Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Migrations.PostgreSQL", "Migrations");

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);

        return actual.FullName;
    }
}
