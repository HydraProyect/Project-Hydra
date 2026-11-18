using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// ADR-013, paso M0b: ancla de intención de las excepciones por tipo del registro de módulos
/// (<c>Modulos/Modulos-registro.txt</c>). Una <c>excepcion</c> saca un tipo o un subnamespace del módulo de su área; si se
/// borra una línea, el tipo vuelve <b>en silencio</b> al módulo de su área y <c>ModulosRegistradosTests</c> sigue verde
/// (todo tipo sigue teniendo módulo). Las excepciones con ancla de tipo en <c>ModulosRegistradosTests.Anclas</c> (N-1,
/// el cierre mensual, el punto de composición…) ya caen ahí; las demás (Tarifas del Operador CAE, Documentos,
/// Integraciones, Web, la siembra…) se anclan aquí, una fila por línea del registro.
///
/// <para>
/// La regla es doble y cierra el hueco en las dos direcciones: <b>toda</b> excepción del registro tiene ancla (de tipo en
/// <c>Anclas</c> o una fila de <see cref="Excepciones"/>), y toda fila de esta tabla es una excepción vigente del
/// registro con ese módulo. Borrar una línea del registro deja una fila huérfana; añadir una excepción sin ancla, una
/// excepción sin fila; cambiar su módulo, una fila que no casa. Cambiar una excepción es, por tanto, un diff en dos sitios.
/// </para>
/// </summary>
public class ExcepcionesDelRegistroDeModulosAncladasTests
{
    /// <summary>Clave de la excepción (sin <c>CaeManager.</c>) y el módulo que da.</summary>
    public static TheoryData<string, Modulo> Excepciones => new()
    {
        { "Domain.Documentos.AcreditacionDocumentoPlataforma", Modulo.Outbound },
        { "Domain.Documentos.VigenciaEnPlataforma", Modulo.Outbound },
        { "Domain.Documentos.ReglaPendienteEnPlataforma", Modulo.Outbound },
        { "Domain.Documentos.CambioEstadoAcreditacionPlataforma", Modulo.Outbound },
        { "Domain.Documentos.EstadoVigenciaEnPlataforma", Modulo.Outbound },
        { "Domain.Documentos.IAcreditacionDocumentoPlataformaRepository", Modulo.Outbound },
        { "Domain.Documentos.OrigenCambioEstadoAcreditacion", Modulo.Outbound },
        { "Domain.Documentos.RechazoAcreditacionDocumentoPlataforma", Modulo.Outbound },
        { "Domain.Integraciones.ConexionPlataformaCae", Modulo.Outbound },
        { "Domain.Integraciones.CredencialConexionPlataformaCae", Modulo.Outbound },
        { "Domain.Integraciones.DominioProveedorPlataformaCae", Modulo.Outbound },
        { "Domain.Integraciones.IConexionPlataformaCaeRepository", Modulo.Outbound },
        { "Domain.Integraciones.IProveedorPlataformaCaeRepository", Modulo.Outbound },
        { "Domain.Integraciones.ProveedorPlataformaCae", Modulo.Outbound },
        { "Domain.Integraciones.UsoConexionPlataformaCae", Modulo.Outbound },
        { "Domain.Dashboard.ConceptoTarifaOperadorCae", Modulo.Facturacion },
        { "Application.Documentos.Acreditacion", Modulo.Outbound },
        { "Application.Documentos.Commands.CambiarEstadoAcreditacionEnCentro", Modulo.Outbound },
        { "Application.Documentos.Commands.ConfirmarVigenciaAcreditacion", Modulo.Outbound },
        { "Application.Documentos.Commands.MarcarAcreditacionAceptada", Modulo.Outbound },
        { "Application.Documentos.Commands.MarcarAcreditacionNoRequerida", Modulo.Outbound },
        { "Application.Documentos.Commands.MarcarAcreditacionPendienteDeSubir", Modulo.Outbound },
        { "Application.Documentos.Commands.MarcarAcreditacionRechazada", Modulo.Outbound },
        { "Application.Documentos.Commands.MarcarAcreditacionSubida", Modulo.Outbound },
        { "Application.Documentos.Commands.RestaurarAnotacionAcreditacion", Modulo.Outbound },
        { "Application.Documentos.Commands.ResultadoAnotacionAcreditacionDto", Modulo.Outbound },
        { "Application.Documentos.Queries.ObtenerAcreditacionesDelDocumento", Modulo.Outbound },
        { "Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor", Modulo.Outbound },
        { "Application.Documentos.Queries.ObtenerPlataformasEnUso", Modulo.Outbound },
        { "Application.Documentos.PortalDelCanalAcreditacion", Modulo.Outbound },
        { "Application.Documentos.ReglaSubidaAcreditacion", Modulo.Outbound },
        { "Application.Integraciones.ConectoresPlataformaCae", Modulo.Outbound },
        { "Application.Integraciones.Commands.CambiarActivoProveedorPlataforma", Modulo.Outbound },
        { "Application.Integraciones.Commands.CambiarConectorApiProveedorPlataforma", Modulo.Outbound },
        { "Application.Integraciones.Commands.GuardarConexionPlataformaCae", Modulo.Outbound },
        { "Application.Integraciones.Commands.RetirarConexionPlataformaCae", Modulo.Outbound },
        { "Application.Integraciones.Commands.AutoridadSobreConexionPlataformaCae", Modulo.Outbound },
        { "Application.Integraciones.Queries.ObtenerProveedoresPlataformaCae", Modulo.Outbound },
        { "Application.Integraciones.IProveedoresPlataformaCaeQueryContext", Modulo.Outbound },
        { "Application.Integraciones.ProveedorPlataformaCaeCandidatoDto", Modulo.Outbound },
        { "Application.Integraciones.IResolucionProveedorPlataformaCaeService", Modulo.Outbound },
        { "Application.Integraciones.ResolucionProveedorPlataformaCaeService", Modulo.Outbound },
        { "Application.Dashboard.TarifasOperadorCae.ITarifasOperadorCaeContext", Modulo.Facturacion },
        { "Application.Dashboard.TarifasOperadorCae.AutorizacionTarifasOperadorCae", Modulo.Facturacion },
        { "Application.Dashboard.TarifasOperadorCae.ObtenerTarifasOperadorCaeQuery", Modulo.Facturacion },
        { "Application.Dashboard.TarifasOperadorCae.ObtenerTarifasOperadorCaeQueryHandler", Modulo.Facturacion },
        { "Application.Dashboard.TarifasOperadorCae.FijarTarifaOperadorCaeCommand", Modulo.Facturacion },
        { "Application.Dashboard.TarifasOperadorCae.FijarTarifaOperadorCaeCommandValidator", Modulo.Facturacion },
        { "Application.Dashboard.TarifasOperadorCae.FijarTarifaOperadorCaeCommandHandler", Modulo.Facturacion },
        { "Application.Dashboard.TarifaOperadorCaeDto", Modulo.Facturacion },
        { "Application.Dashboard.TramoDeVigencia", Modulo.Facturacion },
        { "Application.Dashboard.VigenciaAsignacionOperacion", Modulo.Facturacion },
        { "Application.Dashboard.UnidadesOperadorCae", Modulo.Facturacion },
        { "Application.Dashboard.ValoracionOperadorCae", Modulo.Facturacion },
        { "Application.Dashboard.Queries.ObtenerFacturacionOperadorCaeQuery", Modulo.Facturacion },
        { "Application.Dashboard.Queries.TarifaVigenteOperadorCaeDto", Modulo.Facturacion },
        { "Application.Dashboard.Queries.ConceptoFacturadoOperadorCaeDto", Modulo.Facturacion },
        { "Application.Dashboard.Queries.IngresoPorGestionOperadorCaeDto", Modulo.Facturacion },
        { "Application.Dashboard.Queries.FilaFacturacionOperadorCaeDto", Modulo.Facturacion },
        { "Application.Dashboard.Queries.TotalFacturacionOperadorCaeDto", Modulo.Facturacion },
        { "Application.Dashboard.Queries.FacturacionOperadorCaeDto", Modulo.Facturacion },
        { "Application.Dashboard.Queries.CentrosNuevosPorClienteTotalDto", Modulo.Facturacion },
        { "Application.Dashboard.Queries.CalculosFacturacionOperadorCae", Modulo.Facturacion },
        { "Application.Dashboard.Queries.ObtenerFacturacionOperadorCaeQueryHandler", Modulo.Facturacion },
        { "Application.Dashboard.TarifaAplicadaCuadro", Modulo.Facturacion },
        { "Application.Dashboard.ReabrirMesCuadroDireccionCommandHandler", Modulo.Facturacion },
        { "Application.Clientes.DestinoCartera", Modulo.Nucleo },
        { "Application.Clientes.AccesoDelDestino", Modulo.Nucleo },
        { "Infrastructure.Persistence.Migraciones", Modulo.Composicion },
        { "Infrastructure.Persistence.Repositories.ConversacionRepository", Modulo.Outbound },
        { "Infrastructure.Persistence.SerializacionAltasAcreditacionEmpresa", Modulo.Outbound },
        { "Infrastructure.Documentos.ConciliacionAcreditacionesEmpresaHostedService", Modulo.Outbound },
        { "Infrastructure.Documentos.ConciliacionAcreditacionesEmpresaOptions", Modulo.Outbound },
        { "Infrastructure.Persistence.Seed", Modulo.Outbound },
        { "Infrastructure.Persistence.Seed.TenantSeedData", Modulo.Nucleo },
        { "Infrastructure.Persistence.Seed.IdentityRoleSeedData", Modulo.Nucleo },
        { "Infrastructure.Persistence.Seed.ParametroSistemaSeedData", Modulo.Nucleo },
        { "Infrastructure.Persistence.Seed.TipoDocumentoSeedData", Modulo.Nucleo },
        { "Infrastructure.Persistence.Seed.AceptacionTerminosSeedHelper", Modulo.Nucleo },
        { "Infrastructure.Persistence.Seed.ConocimientoDeteccionCampoSeedData", Modulo.Nucleo },
        { "Infrastructure.Persistence.Seed.SiembraEmpresaPropia", Modulo.Nucleo },
        { "Infrastructure.Persistence.Seed.DelegacionesSoporteSeeder", Modulo.Nucleo },
        { "Infrastructure.Persistence.Seed.LineaInboundDemoSeeder", Modulo.Nucleo },
        { "Infrastructure.Persistence.Seed.InboundDemoSeeder", Modulo.Composicion },
        { "Infrastructure.Persistence.Seed.InboundDemoAdministrativa", Modulo.Composicion },
        { "Web.Components.Layout.CatalogoMenuLateral", Modulo.Composicion },
        { "Web.Api.V1.ClientesApiEndpoints", Modulo.Outbound },
        { "Web.Features.Integraciones.Pages.ConectoresCae", Modulo.Outbound },
        { "Web.Features.CuadroDireccion.Components.DrawerTarifasOperador", Modulo.Facturacion },
        { "Web.Features.CuadroDireccion.Components.FacturacionOperador", Modulo.Facturacion },
        { "Web.Features.Documentos.Components.BadgesAcreditacion", Modulo.Outbound },
        { "Web.Features.Documentos.Components.LineaPlataformaYReclamacion", Modulo.Outbound },
        { "Web.Features.Documentos.Components.MenuAcreditacion", Modulo.Outbound },
        { "Web.Features.Documentos.Components.ModalesAcreditacion", Modulo.Outbound },
        { "Web.Features.Documentos.Components.PanelGestionarAcreditacion", Modulo.Outbound },
        { "Web.Features.Documentos.Components.PlataformaTab", Modulo.Outbound },
        { "Web.Features.Documentos.CausaRechazoAcreditacionUi", Modulo.Outbound },
        { "Web.Features.Documentos.EstadoAcreditacionUi", Modulo.Outbound },
        { "Web.Features.Documentos.GestionarEnPlataforma", Modulo.Outbound },
        { "Web.Features.Documentos.PlataformasCaeDelDocumento", Modulo.Outbound },
        { "Web.Features.Documentos.ReglasAccionesAcreditacion", Modulo.Outbound },
    };

    /// <summary>Las claves de la tabla, para el control de completitud.</summary>
    private static IReadOnlyDictionary<string, Modulo> Tabla() =>
        Excepciones.ToDictionary(fila => (string)fila[0], fila => (Modulo)fila[1], StringComparer.Ordinal);

    [Fact]
    public void La_tabla_de_anclas_tiene_filas_y_sin_repetir()
    {
        Excepciones.Count.Should().BeGreaterThan(50, "control del instrumento: tiene que haber anclas que comprobar");
        Excepciones.Select(f => (string)f[0]).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [MemberData(nameof(Excepciones))]
    public void Cada_excepcion_anclada_sigue_en_el_registro_con_su_modulo_y_gobierna_algun_tipo(string clave, Modulo esperado)
    {
        var registro = RegistroDeModulos.Versionado();
        var entrada = registro.Entradas.SingleOrDefault(e => e.Clase == "excepcion" && e.Clave == clave);

        entrada.Should().NotBeNull(
            $"la excepción '{clave}' ya no está en Modulos-registro.txt: si se borró a propósito, quita también su fila de ancla; " +
            "si no, el tipo ha vuelto en silencio al módulo de su área");
        entrada!.Modulo.Should().Be(esperado, $"la excepción '{clave}' cambió de módulo: actualiza el registro y su ancla juntos");

        var gobernados = TiposDeSrc(registro).Where(t => registro.Clasificar(t)?.Entrada == entrada).ToList();
        gobernados.Should().NotBeEmpty($"la excepción '{clave}' no gobierna ningún tipo: es obsoleta");
    }

    [Fact]
    public void Ninguna_fila_de_la_tabla_esta_huerfana()
    {
        HuerfanasDeAncla(RegistroDeModulos.Versionado(), Tabla().Keys.ToHashSet(StringComparer.Ordinal)).Should().BeEmpty(
            "toda fila de ancla es una excepción vigente del registro; si se borró del registro, quita también su fila");
    }

    [Fact]
    public void Toda_excepcion_del_registro_tiene_ancla()
    {
        var registro = RegistroDeModulos.Versionado();
        var sinAncla = ExcepcionesSinAncla(registro, TiposDeSrc(registro), TiposAnclaDeIntencion(), Tabla().Keys.ToHashSet());

        sinAncla.Should().BeEmpty(
            "toda excepción por tipo del registro tiene ancla: un tipo en ModulosRegistradosTests.Anclas o una fila de " +
            "ExcepcionesDelRegistroDeModulosAncladasTests.Excepciones. Sin ancla:\n  " + string.Join("\n  ", sinAncla));
    }

    [Fact]
    public void Ninguna_fila_de_la_tabla_ancla_lo_que_ya_ancla_un_tipo()
    {
        var registro = RegistroDeModulos.Versionado();
        var tipos = TiposDeSrc(registro);
        var anclados = EntradasAncladasPorTipo(registro, tipos, TiposAnclaDeIntencion()).Select(e => e.Clave).ToHashSet(StringComparer.Ordinal);

        Tabla().Keys.Where(anclados.Contains).Should().BeEmpty("una fila redundante con un ancla de tipo sobra: bórrala");
    }

    // ---- Controles positivos: un registro sintético al que se le borra una línea o se le añade otra. ----

    [Fact]
    public void Control_una_excepcion_nueva_sin_ancla_da_rojo_y_borrar_una_con_ancla_tambien()
    {
        var tipos = TiposDeSrc(RegistroDeModulos.Versionado());
        var conAncla = new HashSet<string>(["Domain.Documentos.EstadoAcreditacion"], StringComparer.Ordinal);

        var original = RegistroDeModulos.Leer("area Domain.Documentos = Nucleo\nexcepcion Domain.Documentos.EstadoAcreditacion = Outbound\n");
        ExcepcionesSinAncla(original, tipos, [], conAncla).Should().BeEmpty();

        var conUnaMas = RegistroDeModulos.Leer(
            "area Domain.Documentos = Nucleo\nexcepcion Domain.Documentos.EstadoAcreditacion = Outbound\n" +
            "excepcion Domain.Documentos.CausaRechazoAcreditacion = Outbound\n");
        ExcepcionesSinAncla(conUnaMas, tipos, [], conAncla).Should().ContainSingle(s => s.Contains("CausaRechazoAcreditacion"));

        var sinLaAnclada = RegistroDeModulos.Leer("area Domain.Documentos = Nucleo\n");
        HuerfanasDeAncla(sinLaAnclada, conAncla).Should().ContainSingle(s => s.Contains("EstadoAcreditacion"),
            "borrar una excepción anclada deja su fila huérfana");
    }

    /// <summary>
    /// El mismo control sobre el registro versionado real, mutado: borrar la línea de una excepción sin ancla de tipo
    /// (Tarifas, Documentos…) deja su fila huérfana, y añadir una excepción que nadie ancla la delata.
    /// </summary>
    [Fact]
    public void Control_borrar_o_anadir_una_excepcion_en_el_registro_real_da_rojo()
    {
        var texto = File.ReadAllText(RegistroDeModulos.RutaVersionada());
        var claves = Tabla().Keys.ToHashSet(StringComparer.Ordinal);
        var lineas = texto.Split('\n');

        var sinLaLinea = lineas.Where(l => !System.Text.RegularExpressions.Regex.IsMatch(
            l, @"^excepcion\s+Domain\.Documentos\.AcreditacionDocumentoPlataforma\s")).ToArray();
        sinLaLinea.Length.Should().Be(lineas.Length - 1, "control del instrumento: la línea existe una sola vez");
        HuerfanasDeAncla(RegistroDeModulos.Leer(string.Join('\n', sinLaLinea)), claves)
            .Should().ContainSingle(s => s.Contains("AcreditacionDocumentoPlataforma"));

        var conUnaMas = RegistroDeModulos.Leer(texto + "\nexcepcion Application.ControlDeExcepcionSinAncla = Outbound\n");
        var registro = RegistroDeModulos.Versionado();
        ExcepcionesSinAncla(conUnaMas, TiposDeSrc(registro), TiposAnclaDeIntencion(), claves)
            .Should().ContainSingle(s => s.Contains("ControlDeExcepcionSinAncla"));
    }

    // ---- Soporte ----

    private static IReadOnlyList<Type> TiposDeSrc(RegistroDeModulos registro) =>
        RegistroDeModulos.TiposAClasificar(RegistroDeModulos.EnsambladosDeSrc()).Where(t => !registro.EsAjeno(t)).ToList();

    /// <summary>Los tipos de <c>ModulosRegistradosTests.Anclas</c> (nombre completo), que anclan por tipo.</summary>
    private static IReadOnlyList<Type> TiposAnclaDeIntencion() =>
        ModulosRegistradosTests.Anclas
            .Select(fila => (string)fila[0])
            .Select(nombre => RegistroDeModulos.EnsambladosDeSrc().Select(a => a.GetType(nombre, throwOnError: false)).SingleOrDefault(t => t is not null))
            .Where(t => t is not null)
            .Select(t => t!)
            .ToList();

    private static HashSet<EntradaDeRegistro> EntradasAncladasPorTipo(
        RegistroDeModulos registro, IReadOnlyList<Type> tipos, IReadOnlyList<Type> tiposAncla)
    {
        var existentes = tipos.ToHashSet();
        return tiposAncla.Where(existentes.Contains)
            .Select(t => registro.Clasificar(t)?.Entrada)
            .Where(e => e is { Clase: "excepcion" })
            .Select(e => e!)
            .ToHashSet();
    }

    private static List<string> ExcepcionesSinAncla(
        RegistroDeModulos registro, IReadOnlyList<Type> tipos, IReadOnlyList<Type> tiposAncla, IReadOnlySet<string> clavesDeTabla)
    {
        var porTipo = EntradasAncladasPorTipo(registro, tipos, tiposAncla);
        return registro.Entradas
            .Where(e => e.Clase == "excepcion" && !porTipo.Contains(e) && !clavesDeTabla.Contains(e.Clave))
            .Select(e => e.ToString())
            .ToList();
    }

    private static List<string> HuerfanasDeAncla(RegistroDeModulos registro, IReadOnlySet<string> clavesDeTabla)
    {
        var vigentes = registro.Entradas.Where(e => e.Clase == "excepcion").Select(e => e.Clave).ToHashSet(StringComparer.Ordinal);
        return clavesDeTabla.Where(c => !vigentes.Contains(c)).Select(c => $"fila de ancla sin excepción: {c}").ToList();
    }
}
