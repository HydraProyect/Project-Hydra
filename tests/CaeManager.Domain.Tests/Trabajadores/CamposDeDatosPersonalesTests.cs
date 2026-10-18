using System.Reflection;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Trabajadores;

/// <summary>
/// <see cref="Trabajador.CamposDeDatosPersonales"/> decide qué se enmascara en el historial de
/// auditoría al anonimizar (AuditoriaInterceptor y la función
/// app_enmascarar_auditoria_de_trabajador_anonimizado). Trinquete: una propiedad nueva del
/// Trabajador tiene que declararse aquí como dato personal o como no personal, y todo dato
/// personal tiene que vaciarlo <see cref="Trabajador.Anonimizar"/>.
/// </summary>
public class CamposDeDatosPersonalesTests
{
    private static readonly string[] PropiedadesNoPersonales =
    [
        nameof(Trabajador.EmpresaId), nameof(Trabajador.SubcontrataId), nameof(Trabajador.AnonimizadoEnUtc),
        // Enum cerrado de tres valores que gobierna reglas operativas (voluntario inactivo); a diferencia de TipoVinculo,
        // que es texto libre, no puede contener nada que identifique a la persona una vez suprimidos nombre y documento.
        nameof(Trabajador.TipoContratacion),
    ];

    [Fact]
    public void Toda_propiedad_propia_del_trabajador_esta_clasificada()
    {
        var propias = typeof(Trabajador)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.SetMethod is not null)
            .Select(p => p.Name);

        propias.Should().BeEquivalentTo(Trabajador.CamposDeDatosPersonales.Concat(PropiedadesNoPersonales),
            "una propiedad nueva se clasifica: si es personal va a CamposDeDatosPersonales, a Anonimizar y a la función SQL");
    }

    [Fact]
    public void Anonimizar_vacia_cada_campo_de_datos_personales()
    {
        var trabajador = Trabajador.DeEmpresa(
            Guid.NewGuid(), "Manuela", "Quintanilla Ortega", "11111111H", new DateOnly(1985, 3, 4),
            "manuela@ejemplo.test", "Observación", "Manu", "+34600111222", "Soldadora");
        trabajador.ActualizarFicha("281234567890", "Soldadora", new DateOnly(1985, 3, 4), "Española", "Indefinido",
            new DateOnly(2020, 1, 1), new DateOnly(2024, 1, 1), "Sí", "Cuenta ajena", new DateOnly(2030, 1, 1), "Arraigo");
        trabajador.FijarNotaInterna("Manuela prefiere que la llamen por la tarde.");
        trabajador.FijarContactoEmergencia("Andrés Quintanilla (hermano)", "+34 611 222 333");

        trabajador.Anonimizar(DateTime.UtcNow);

        foreach (var campo in Trabajador.CamposDeDatosPersonales)
        {
            var valor = typeof(Trabajador).GetProperty(campo)!.GetValue(trabajador);
            if (campo == nameof(Trabajador.Nombre))
                valor.Should().Be($"Anonimizado {trabajador.Id.ToString()[..8]}");
            else
                (valor is null || valor as string == string.Empty).Should().BeTrue($"{campo} es un dato personal");
        }
    }
}
