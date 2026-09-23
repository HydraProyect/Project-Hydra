using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using FluentAssertions;

namespace CaeManager.Application.Tests.Trabajadores;

/// <summary>
/// Sin DNI (P4, 2026-09-23), la etiqueta de un selector de Trabajador tiene que seguir siendo única
/// dentro de su lista: los buscadores traducen el texto elegido a un Id, y dos opciones con el
/// mismo texto harían que un Documento acabara en el Trabajador homónimo.
/// </summary>
public class EtiquetasSelectorTrabajadorTests
{
    private static readonly Guid IdA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid IdB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid IdC = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    private static Dictionary<Guid, string> Etiquetas(bool aliasSiempre, params TrabajadorSelectorDto[] trabajadores) =>
        EtiquetasSelectorTrabajador.Construir(trabajadores, aliasSiempre).ToDictionary(e => e.Id, e => e.Texto);

    [Fact]
    public void Un_nombre_unico_en_la_lista_es_solo_el_nombre()
    {
        var etiquetas = Etiquetas(false,
            new TrabajadorSelectorDto(IdA, "Ana Ruiz", "Anita", "Montajes Ebro S.L."),
            new TrabajadorSelectorDto(IdB, "Luis Gil", null, "Montajes Ebro S.L."));

        etiquetas.Should().Equal(new Dictionary<Guid, string> { [IdA] = "Ana Ruiz", [IdB] = "Luis Gil" });
    }

    [Fact]
    public void Homonimos_con_distinto_empleador_se_distinguen_por_el_empleador()
    {
        var etiquetas = Etiquetas(false,
            new TrabajadorSelectorDto(IdA, "Ana Ruiz", null, "Montajes Ebro S.L."),
            new TrabajadorSelectorDto(IdB, "Ana Ruiz", null, "Instalaciones Arbeko S.L."),
            new TrabajadorSelectorDto(IdC, "Luis Gil", null, "Montajes Ebro S.L."));

        etiquetas[IdA].Should().Be("Ana Ruiz (Montajes Ebro S.L.)");
        etiquetas[IdB].Should().Be("Ana Ruiz (Instalaciones Arbeko S.L.)");
        etiquetas[IdC].Should().Be("Luis Gil", "solo se desambigua el grupo que colisiona");
    }

    [Fact]
    public void Homonimos_con_el_mismo_empleador_se_distinguen_por_el_alias()
    {
        var etiquetas = Etiquetas(false,
            new TrabajadorSelectorDto(IdA, "Ana Ruiz", "Anita", "Montajes Ebro S.L."),
            new TrabajadorSelectorDto(IdB, "Ana Ruiz", "Nani", "Montajes Ebro S.L."));

        etiquetas[IdA].Should().Be("Ana Ruiz — Anita");
        etiquetas[IdB].Should().Be("Ana Ruiz — Nani");
    }

    [Fact]
    public void Homonimos_con_el_mismo_empleador_y_sin_alias_llevan_un_sufijo_determinista_por_Id()
    {
        // Orden de entrada invertido a propósito: el sufijo sale del Id, no de la posición.
        var etiquetas = Etiquetas(false,
            new TrabajadorSelectorDto(IdB, "Ana Ruiz", null, "Montajes Ebro S.L."),
            new TrabajadorSelectorDto(IdA, "Ana Ruiz", null, "Montajes Ebro S.L."));

        etiquetas[IdA].Should().Be("Ana Ruiz (Montajes Ebro S.L.) [1]");
        etiquetas[IdB].Should().Be("Ana Ruiz (Montajes Ebro S.L.) [2]");
    }

    [Fact]
    public void Con_alias_siempre_el_alias_entra_aunque_el_nombre_sea_unico()
    {
        var etiquetas = Etiquetas(true,
            new TrabajadorSelectorDto(IdA, "Ana Ruiz", "Anita", "Montajes Ebro S.L."),
            new TrabajadorSelectorDto(IdB, "Luis Gil", null, null));

        etiquetas[IdA].Should().Be("Ana Ruiz — Anita");
        etiquetas[IdB].Should().Be("Luis Gil");
    }

    [Fact]
    public void Ninguna_combinacion_deja_dos_etiquetas_iguales()
    {
        var etiquetas = EtiquetasSelectorTrabajador.Construir(
        [
            new TrabajadorSelectorDto(IdA, "Ana Ruiz", null, null),
            new TrabajadorSelectorDto(IdB, "Ana Ruiz", null, null),
            // Nombre literal que coincide con la etiqueta con sufijo de otro.
            new TrabajadorSelectorDto(IdC, "Ana Ruiz [1]", null, null),
        ]);

        etiquetas.Select(e => e.Texto).Should().OnlyHaveUniqueItems();
        etiquetas.Select(e => e.Id).Should().Equal([IdA, IdB, IdC], "se conserva el orden de la lista");
    }

    /// <summary>
    /// Caso adversario (revisión Codex, ronda 1): un nombre literal igual a la etiqueta que el
    /// último recurso genera para otro Trabajador, con su Id incluido. Cada etiqueta tiene que
    /// resolver exactamente a un Trabajador.
    /// </summary>
    [Fact]
    public void Un_nombre_literal_igual_a_la_etiqueta_con_Id_de_otro_no_deja_duplicados()
    {
        var idD = Guid.Parse("00000000-0000-0000-0000-00000000000d");
        var trabajadores = new[]
        {
            new TrabajadorSelectorDto(IdA, "Ana Ruiz", null, null),
            new TrabajadorSelectorDto(IdB, "Ana Ruiz", null, null),
            new TrabajadorSelectorDto(IdC, "Ana Ruiz [1]", null, null),
            new TrabajadorSelectorDto(idD, $"Ana Ruiz [1] [{IdA:N}]", null, null),
        };

        var etiquetas = EtiquetasSelectorTrabajador.Construir(trabajadores);

        etiquetas.Select(e => e.Texto).Should().OnlyHaveUniqueItems();
        foreach (var etiqueta in etiquetas)
            etiquetas.Where(e => e.Texto == etiqueta.Texto).Select(e => e.Id).Should().Equal([etiqueta.Id],
                $"«{etiqueta.Texto}» tiene que resolver solo a su Trabajador");
    }
}
