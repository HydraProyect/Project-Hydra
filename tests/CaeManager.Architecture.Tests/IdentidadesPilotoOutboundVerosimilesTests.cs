using System.Text.RegularExpressions;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Xunit.Abstractions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Los nombres, identificadores y direcciones con que la siembra del piloto Outbound
/// rotula a las personas, las Empresas y los Centros de Trabajo
/// (<see cref="IdentidadesPilotoOutbound"/>) se sostienen a la vista: nadie se llama
/// igual que otro, ningún identificador delata que salió de un contador y ninguna
/// etiqueta enseña la clave de la matriz.
///
/// <para>
/// Son funciones puras del catálogo, así que se recorren enteras y sin base de datos:
/// los seis Tenants, todos sus Trabajadores (los de las subcontratas de T1 incluidos),
/// todas sus Empresas y todos sus Centros. Lo que queda escrito en la base lo miden
/// los tests de integración de la siembra.
/// </para>
/// </summary>
public partial class IdentidadesPilotoOutboundVerosimilesTests(ITestOutputHelper salida)
{
    private static IReadOnlyList<TenantPilotoOutbound> Tenants => CatalogoPilotoOutbound.Tenants;

    private sealed record TrabajadorDelLote(TenantPilotoOutbound Tenant, int Indice, string Nombre, string Apellidos, string Dni)
    {
        public string NombreCompleto => $"{Nombre} {Apellidos}";
    }

    private static List<TrabajadorDelLote> TrabajadoresDelLote() =>
    [
        .. from tenant in Tenants
           from i in Enumerable.Range(0, CatalogoPilotoOutbound.TrabajadoresSembrados(tenant))
           let persona = IdentidadesPilotoOutbound.Trabajador(tenant, i)
           select new TrabajadorDelLote(tenant, i, persona.Nombre, persona.Apellidos, IdentidadesPilotoOutbound.Dni(tenant, i))
    ];

    /// <summary>Cada Empresa del lote con el ordinal con que la siembra le pide el identificador fiscal.</summary>
    private static List<(TenantPilotoOutbound Tenant, string RazonSocial, string Cif)> EmpresasDelLote() =>
    [
        .. from tenant in Tenants
           from empresa in new[] { (Ordinal: 0, RazonSocial: tenant.Nombre) }
               .Concat(tenant.ClientesEmpresariales.Select((c, i) => (Ordinal: i + 1, c.RazonSocial)))
               .Concat((tenant.Subcontratas ?? []).Select((s, i) => (Ordinal: IdentidadesPilotoOutbound.OrdinalDeLaPrimeraSubcontrata + i, RazonSocial: s)))
           select (tenant, empresa.RazonSocial, IdentidadesPilotoOutbound.Cif(tenant, empresa.Ordinal))
    ];

    [Fact]
    public void Ningun_Trabajador_del_lote_repite_nombre_completo_y_ninguna_pareja_de_apellidos_domina()
    {
        var trabajadores = TrabajadoresDelLote();
        trabajadores.Should().HaveCountGreaterThan(200, "el lote tiene los 175 Trabajadores de T1 y los de los otros cinco Tenants");

        trabajadores.GroupBy(t => t.NombreCompleto).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(t => $"{t.Tenant.Clave}[{t.Indice}]"))}")
            .Should().BeEmpty("ningún nombre completo se repite entre los Trabajadores de los seis Tenants");

        // El criterio de «no domina», en su forma más estricta: una pareja de apellidos la lleva UN Trabajador del lote.
        trabajadores.GroupBy(t => t.Apellidos).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(t => $"{t.Tenant.Clave}[{t.Indice}]"))}")
            .Should().BeEmpty("ninguna pareja de primer y segundo apellido se repite entre los Trabajadores del lote");

        // Y un apellido suelto tampoco llena una pantalla: en un Tenant de veinte o más, nadie comparte el primero con más de uno de cada diez.
        foreach (var delTenant in trabajadores.GroupBy(t => t.Tenant).Where(g => g.Count() >= 20))
        {
            var masLlevado = delTenant.GroupBy(t => t.Apellidos.Split(' ')[0]).MaxBy(g => g.Count())!;
            salida.WriteLine(
                $"MEDIDO {delTenant.Key.Clave}: {delTenant.Count()} Trabajadores; el primer apellido más llevado es «{masLlevado.Key}», con {masLlevado.Count()}");
            masLlevado.Count().Should().BeLessThanOrEqualTo(
                delTenant.Count() / 10, $"en {delTenant.Key.Clave} ningún primer apellido lo lleva más de uno de cada diez Trabajadores");
        }

        foreach (var t in trabajadores.Where(t =>
                     (t.Tenant == CatalogoPilotoOutbound.T5)
                     || (t.Tenant == CatalogoPilotoOutbound.T6 && t.Indice == DisenoT6PilotoOutbound.TrabajadorDesplazado)
                     || (t.Tenant == CatalogoPilotoOutbound.T1 && DisenoT1PilotoOutbound.TrabajadoresDeLaVisita.Contains(t.Indice))))
            salida.WriteLine($"MEDIDO {t.Tenant.Clave}[{t.Indice}]: {t.NombreCompleto} · {t.Dni} · {IdentidadesPilotoOutbound.Puesto(t.Tenant, t.Indice)}");
    }

    [Fact]
    public void Los_contactos_de_agenda_de_un_Tenant_son_personas_distintas_y_ninguno_se_llama_como_un_Trabajador()
    {
        var nombresDeTrabajador = TrabajadoresDelLote().Select(t => t.NombreCompleto).ToHashSet();
        var apellidosDeTrabajador = TrabajadoresDelLote().Select(t => t.Apellidos).ToHashSet();

        foreach (var tenant in Tenants)
        {
            // Los ordinales que usa la siembra van de 40 a 98; se recorren doscientos, que cubren cualquier tramo que se añada.
            var contactos = Enumerable.Range(0, 200).Select(o => IdentidadesPilotoOutbound.Contacto(tenant, o)).ToList();

            contactos.Should().OnlyHaveUniqueItems($"dos contactos de agenda de {tenant.Clave} no son la misma persona");
            contactos.Where(nombresDeTrabajador.Contains).Should().BeEmpty($"ningún contacto de {tenant.Clave} se llama como un Trabajador del lote");
            contactos.Select(c => c[(c.IndexOf(' ') + 1)..]).Where(apellidosDeTrabajador.Contains)
                .Should().BeEmpty($"ningún contacto de {tenant.Clave} comparte los dos apellidos con un Trabajador del lote");
        }
    }

    [Fact]
    public void Los_documentos_de_identidad_son_validos_unicos_y_no_correlativos()
    {
        var trabajadores = TrabajadoresDelLote();

        foreach (var t in trabajadores)
        {
            var analisis = ValidadorIdentificacion.Analizar(t.Dni);
            (analisis.EsValido, analisis.Tipo).Should().Be((true, TipoIdentificacion.Dni), $"{t.Dni}, de {t.Tenant.Clave}[{t.Indice}], es un DNI con su letra de control");
        }

        trabajadores.Select(t => t.Dni).Should().OnlyHaveUniqueItems("ningún documento de identidad se repite en el lote");

        var numeros = trabajadores.Select(t => long.Parse(t.Dni[..8])).ToList();
        var distanciaMinimaEntreSeguidos = numeros.Zip(numeros.Skip(1), (a, b) => Math.Abs(a - b)).Min();
        var distanciaMinima = numeros.Order().Zip(numeros.Order().Skip(1), (a, b) => b - a).Min();
        salida.WriteLine($"MEDIDO: entre dos Trabajadores seguidos, los números distan al menos {distanciaMinimaEntreSeguidos:N0}; entre dos cualesquiera, {distanciaMinima:N0}");

        distanciaMinimaEntreSeguidos.Should().BeGreaterThan(1_000_000, "dos Trabajadores seguidos no tienen números de documento vecinos");
        distanciaMinima.Should().BeGreaterThan(1_000, "ningún par de documentos del lote parece salido de un contador");
    }

    [Fact]
    public void Los_identificadores_fiscales_son_validos_unicos_no_correlativos_y_llevan_la_letra_de_su_forma_juridica()
    {
        var empresas = EmpresasDelLote();
        empresas.Should().HaveCountGreaterThan(40);

        foreach (var (tenant, razonSocial, cif) in empresas)
        {
            var analisis = ValidadorIdentificacion.Analizar(cif);
            (analisis.EsValido, analisis.Tipo).Should().Be(
                (true, TipoIdentificacion.NifEmpresa), $"{cif}, de «{razonSocial}» ({tenant.Clave}), es un identificador fiscal de sociedad con su dígito de control");

            var letra = razonSocial.EndsWith("S.A.", StringComparison.Ordinal) ? 'A' : 'B';
            cif[0].Should().Be(letra, $"«{razonSocial}» es una sociedad {(letra == 'A' ? "anónima" : "limitada")}");
        }

        empresas.Select(e => e.RazonSocial).Should().OnlyContain(
            r => r.EndsWith(", S.A.", StringComparison.Ordinal) || r.EndsWith(", S.L.", StringComparison.Ordinal),
            "el catálogo solo usa las dos formas jurídicas a las que la siembra sabe dar letra");
        empresas.Select(e => e.Cif).Should().OnlyHaveUniqueItems("ningún identificador fiscal se repite en el lote");
        empresas.Select(e => e.Cif[0]).Distinct().Should().BeEquivalentTo(['A', 'B'], "el catálogo tiene sociedades anónimas y limitadas");

        foreach (var delTenant in empresas.GroupBy(e => e.Tenant))
        {
            var numeros = delTenant.Select(e => long.Parse(e.Cif[1..8])).Order().ToList();
            numeros.Zip(numeros.Skip(1), (a, b) => b - a).Min().Should().BeGreaterThan(
                100, $"las Empresas de {delTenant.Key.Clave} no tienen identificadores fiscales correlativos");
        }

        salida.WriteLine("MEDIDO: " + string.Join(" · ", Tenants.Select(t => $"{t.Clave} {IdentidadesPilotoOutbound.Cif(t, 0)}")));
    }

    [GeneratedRegex(@"^[A-Z]{3}-\d{2}$")]
    private static partial Regex FormaDelCodigoDeCentro();

    [Fact]
    public void El_codigo_de_un_Centro_no_ensena_la_clave_de_la_matriz_y_no_se_repite_en_su_Tenant()
    {
        foreach (var tenant in Tenants)
        {
            var codigos = tenant.Centros.Select(tenant.CodigoDe).ToList();

            codigos.Should().OnlyContain(c => FormaDelCodigoDeCentro().IsMatch(c), $"un código de Centro de {tenant.Clave} son tres letras y un número de orden");
            codigos.Should().OnlyHaveUniqueItems($"ningún código de Centro se repite en {tenant.Clave}");

            foreach (var centro in tenant.Centros.Where(c => c.Zona is not null))
                tenant.CodigoDe(centro).Should().StartWith(centro.Zona!.Codigo + "-", "un Centro con zona la lleva en el código");

            salida.WriteLine($"MEDIDO {tenant.Clave}: {string.Join(", ", codigos)}");
        }

        // El test de integración de T3 recorre sus Centros por código y espera encontrarlos en el orden del catálogo.
        CatalogoPilotoOutbound.T3.Centros.Select(CatalogoPilotoOutbound.T3.CodigoDe).Should().BeInAscendingOrder(
            StringComparer.Ordinal, "en T3, el orden de los códigos es el orden de los Centros en el catálogo");
    }

    [GeneratedRegex(@"^[a-z0-9]+([.-][a-z0-9]+)*$")]
    private static partial Regex FormaDeLaEtiqueta();

    // «centro» a secas es una palabra del nombre de un Centro («centro-distribucion-getafe»); con número, es la matriz.
    [GeneratedRegex(@"^(t\d+|propia|centro\d+|cliente\d+|subcontrata\d+)$")]
    private static partial Regex PalabraDeLaMatriz();

    [Fact]
    public void Las_direcciones_de_correo_no_ensenan_la_matriz_y_distinguen_a_cada_contacto_del_lote()
    {
        var delLote = new List<string>();
        foreach (var tenant in Tenants)
        {
            List<string> etiquetas =
            [
                IdentidadesPilotoOutbound.EtiquetaDePrevencion(tenant.Nombre),
                IdentidadesPilotoOutbound.EtiquetaDeAdministracion(tenant.Nombre),
                .. tenant.Centros.Select(c => c.Zona).OfType<ZonaPilotoOutbound>().Distinct()
                    .Select(z => IdentidadesPilotoOutbound.EtiquetaDeCoordinacionDeZona(z, tenant.Nombre)),
                .. tenant.ClientesEmpresariales.Select(c => IdentidadesPilotoOutbound.EtiquetaDeCoordinacionCae(c.RazonSocial)),
                .. (tenant.Subcontratas ?? []).Select(IdentidadesPilotoOutbound.EtiquetaDePrevencion),
                .. tenant.Centros.Select(IdentidadesPilotoOutbound.EtiquetaDeCoordinacionDeAccesos),
                .. tenant.Centros.Select(IdentidadesPilotoOutbound.EtiquetaDeSolicitudesDeAcceso)
            ];

            delLote.AddRange(etiquetas);
            etiquetas.Should().OnlyContain(e => FormaDeLaEtiqueta().IsMatch(e), "la parte local de una dirección son palabras en minúsculas, sin acentos, unidas por puntos o guiones");
            etiquetas.SelectMany(e => e.Split('.', '-')).Where(p => PalabraDeLaMatriz().IsMatch(p))
                .Should().BeEmpty($"ninguna dirección de {tenant.Clave} lleva la clave del Tenant ni una palabra de la matriz («propia», «centro14», «cliente3»…)");
            etiquetas.Should().OnlyContain(e => e.Length <= 64, "la parte local de una dirección de correo no pasa de 64 caracteres");

            IdentidadesPilotoOutbound.UsuarioDePortal(tenant.Nombre).Split('.', '-').Where(p => PalabraDeLaMatriz().IsMatch(p))
                .Should().BeEmpty($"el usuario de portal de {tenant.Clave} tampoco lleva la clave del Tenant");

            salida.WriteLine($"MEDIDO {tenant.Clave}: {etiquetas[0]} · {etiquetas[^1]}");
        }

        // Con un buzón único configurado, todas las direcciones del lote son variantes «+etiqueta» de la misma: la
        // etiqueta es lo que dice a qué contacto, de cuál de los seis Tenants, se escribió.
        delLote.GroupBy(e => e).Where(g => g.Count() > 1).Select(g => g.Key)
            .Should().BeEmpty("ninguna dirección se repite en el lote: cada contacto y cada canal, de cualquier Tenant, tiene la suya");
    }
}
