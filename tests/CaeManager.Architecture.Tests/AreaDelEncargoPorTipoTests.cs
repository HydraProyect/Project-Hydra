using System.Runtime.CompilerServices;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Architecture.Tests
{
    /// <summary>
    /// <b>El área de <see cref="ActosExcluidosDelEncargo"/> se resuelve por tipo, no por la carpeta</b> (ADR-013, paso
    /// M0c). Antes, el área era el primer segmento del espacio de nombres tras <c>CaeManager.Application.</c>: mover
    /// <c>AutorizacionesLectura</c> a <c>CaeManager.Application.Inbound.*</c> (paso M1) le habría dado el área
    /// «Inbound», que no está excluida, y la habría abierto a quien administra por Encargo de administración no pleno.
    ///
    /// <para>
    /// <b>Lo que observa</b>: por reflexión, toda petición concreta del ensamblado de Application, su
    /// <see cref="AreaDelEncargoAttribute"/>, el módulo que le da el registro de M0a (<see cref="RegistroDeModulos"/>,
    /// solo desde tests) y el resultado de <see cref="ActosExcluidosDelEncargo.Excluye"/> sobre una instancia sin
    /// inicializar. <b>Lo que NO observa</b>: si la clasificación de un área es acertada —eso es una decisión (D-8)—,
    /// ni el comportamiento del behavior en ejecución, que fijan los tests de Application.
    /// </para>
    /// </summary>
    public class AreaDelEncargoPorTipoTests
    {
        private static readonly Type[] TiposDeApplication = typeof(ActosExcluidosDelEncargo).Assembly.GetTypes();

        /// <summary>
        /// Las peticiones concretas e instanciables. Una definición genérica abierta no se puede instanciar sin
        /// inicializar (no es un tipo cerrado); la vigila aparte <see cref="Ninguna_peticion_generica_abierta_queda_sin_marca"/>.
        /// </summary>
        private static readonly List<Type> Peticiones = TiposDeApplication
            .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false }
                        && typeof(IBaseRequest).IsAssignableFrom(t))
            .ToList();

        /// <summary>
        /// El conjunto de peticiones que <see cref="ActosExcluidosDelEncargo.Excluye"/> deja fuera del encargo no pleno,
        /// congelado del resultado de <c>main</c> en 05e5e1a0d (antes de M0c). Va por nombre sin espacio de nombres para
        /// que sobreviva a M1: mover una petición no debe tocar esta lista. <c>ObtenerAuditoriaQuery</c> entra
        /// porque sin inicializar lleva <c>EntidadId</c> nulo, su variante excluida.
        /// </summary>
        private static readonly string[] ExcluidasCongeladas =
        [
            "ActualizarDeteccionTrabajadoresGlobalCommand",
            "ActualizarLecturaIaGlobalCommand",
            "ActualizarLineaWhatsAppCommand",
            "ActualizarPerfilDocumentoOficialGlobalCommand",
            "ActualizarPresupuestoIaCommand",
            "ActualizarSeguridadAccesoCommand",
            "ActualizarTarifaClienteCommand",
            "ActualizarVerificacionIaGlobalCommand",
            "AnalizarImportacionExcelQuery",
            "AnalizarPlantillaClientesQuery",
            "AnalizarPlantillaCombinadaQuery",
            "AnalizarPlantillaDocumentosQuery",
            "AnalizarPlantillaMaestraQuery",
            "AsignarTrabajadoresAutorizacionCommand",
            "BuscarDatosPurgablesCommand",
            "CancelarPurgaCommand",
            "ConcederAutorizacionLecturaCommand",
            "ConectarBuzonMicrosoft365Command",
            "ConfirmarYEnviarPrimerAvisoCommand",
            "CrearLineaWhatsAppCommand",
            "CrearTarifaClienteCommand",
            "DecidirIncidenteSeguridadCommand",
            "DesconectarBuzonCommand",
            "DiagnosticarDatosPurgablesCommand",
            "EjecutarImportacionCombinadaCommand",
            "EjecutarImportacionCommand",
            "EjecutarImportacionMaestraCommand",
            "EjecutarPurgaCommand",
            "EliminarTarifaClienteCommand",
            "EnviarNotificacionCompletaCommand",
            "FijarAcuseAutomaticoBuzonCommand",
            "FijarResponsableBuzonCommand",
            "GenerarClaveApiCommand",
            "GuardarConexionPlataformaCaeCommand",
            "MarcarCobroLiquidacionOperadorCaeCommand",
            "MarcarTenantAvisadoCommand",
            "ObtenerAccesosDocumentosSensiblesQuery",
            "ObtenerAuditoriaIaQuery",
            "ObtenerAuditoriaQuery",
            "ObtenerAutorizacionesLecturaQuery",
            "ObtenerClavesApiDeMiOrganizacionQuery",
            "ObtenerClavesApiQuery",
            "ObtenerCobrosLiquidacionOperadorCaeQuery",
            "ObtenerContactoSeguridadDeMiOrganizacionQuery",
            "ObtenerContactoSeguridadVigenteQuery",
            "ObtenerContactosSeguridadPropuestosQuery",
            "ObtenerHistorialImportacionesQuery",
            "ObtenerIncidenteSeguridadQuery",
            "ObtenerLineasWhatsAppQuery",
            "ObtenerRegistroAuditoriaPorIdQuery",
            "ObtenerResumenFacturacionQuery",
            "ObtenerSolicitudesPurgaQuery",
            "ObtenerTarifasClienteQuery",
            "PrevisualizarImportacionMaestraCommand",
            "ProgramarPurgaCommand",
            "ProponerContactoSeguridadCommand",
            "ReactivarConexionCommand",
            "RechazarContactoSeguridadCommand",
            "RegistrarHistorialImportacionCommand",
            "RepararMarcaHuecoCommand",
            "ResolverDestinatariosAvisoSeguridadQuery",
            "RetirarConexionPlataformaCaeCommand",
            "RetirarTrabajadorAutorizacionCommand",
            "RevocarAutorizacionLecturaCommand",
            "RevocarClaveApiCommand",
            "RevocarClaveApiDeMiOrganizacionCommand",
            "ValidarContactoSeguridadCommand",
        ];

        private static string NombreCorto(Type t) => t.FullName![(t.Namespace!.Length + 1)..];

        private static bool ExcluyeSinInicializar(Type t) =>
            ActosExcluidosDelEncargo.Excluye(RuntimeHelpers.GetUninitializedObject(t));

        [Fact]
        public void El_conjunto_excluido_es_identico_al_congelado_de_main()
        {
            Peticiones.Should().HaveCountGreaterThan(300, "control positivo: el detector ve las peticiones de Application");
            Peticiones.Select(NombreCorto).Should().OnlyHaveUniqueItems(
                "la lista congelada identifica cada petición por su nombre sin espacio de nombres");

            Peticiones.Where(ExcluyeSinInicializar).Select(NombreCorto).Should().BeEquivalentTo(ExcluidasCongeladas,
                "M0c cambia cómo se resuelve el área, no qué queda excluido; una petición nueva excluida o una que deja "
                + "de estarlo es una decisión (D-8) que se escribe aquí en el mismo commit");
        }

        [Fact]
        public void Cada_peticion_Inbound_lleva_su_area_en_el_tipo_y_su_exclusion_queda_fijada()
        {
            var registro = RegistroDeModulos.Versionado();
            var inbound = Peticiones.Where(p => registro.Clasificar(p)?.Modulo == Modulo.Inbound).ToList();
            inbound.Should().HaveCountGreaterThan(20,
                "control positivo: ContratosAcceso, AutorizacionesLectura y CatalogoBase tienen peticiones Inbound");

            inbound.Should().OnlyContain(p => Marca(p) != null,
                "una petición Inbound declara su área en el tipo: M1 la moverá a CaeManager.Application.Inbound.*");
            inbound.Where(ExcluyeSinInicializar).Select(NombreCorto).Should().BeEquivalentTo(
                inbound.Select(NombreCorto).Intersect(ExcluidasCongeladas),
                "la exclusión de cada petición Inbound es la congelada");
            inbound.Where(p => Marca(p) == "AutorizacionesLectura").Should().NotBeEmpty()
                .And.OnlyContain(p => ExcluyeSinInicializar(p),
                    "las autorizaciones de lectura están excluidas enteras del encargo no pleno");
        }

        /// <summary>
        /// Llevan la marca las peticiones de un área excluida entera (lo que más cuesta abrir por accidente) y las de los
        /// módulos Inbound y Facturación (lo que ADR-013 prevé mover). Mientras una petición no se haya movido, su marca
        /// coincide con su carpeta; cuando el espacio de nombres deje de ser un área (por ejemplo «Inbound»), manda la
        /// marca. Si una petición marcada acaba en la carpeta de otra área clasificada, este test se pone en rojo: es una
        /// reclasificación y se decide, no se hereda de la carpeta.
        /// </summary>
        [Fact]
        public void Las_peticiones_que_deben_llevar_la_marca_la_llevan_y_coincide_con_su_carpeta()
        {
            var registro = RegistroDeModulos.Versionado();
            var obligadas = Peticiones
                .Where(p => ActosExcluidosDelEncargo.AreaPorEspacioDeNombres(p) is { } a && ActosExcluidosDelEncargo.AreasExcluidas.Contains(a)
                            || registro.Clasificar(p)?.Modulo is Modulo.Inbound or Modulo.Facturacion)
                .ToList();
            obligadas.Should().HaveCountGreaterThan(60, "control positivo: siete áreas excluidas y los módulos Inbound y Facturación");

            obligadas.Where(p => Marca(p) is null).Select(p => p.FullName).Should().BeEmpty(
                "estas peticiones declaran su área con [AreaDelEncargo(\"…\")] en el tipo");

            var marcadas = Peticiones.Where(p => Marca(p) != null).ToList();
            marcadas.Should().OnlyContain(p => ActosExcluidosDelEncargo.AreasExcluidas.Contains(Marca(p)!)
                                               || ActosExcluidosDelEncargo.AreasPermitidas.Contains(Marca(p)!),
                "la marca nombra un área clasificada; una que no lo está deja la petición sin área y excluida");
            marcadas
                .Where(p => ActosExcluidosDelEncargo.AreaPorEspacioDeNombres(p) is { } a
                            && (ActosExcluidosDelEncargo.AreasExcluidas.Contains(a) || ActosExcluidosDelEncargo.AreasPermitidas.Contains(a)))
                .Where(p => ActosExcluidosDelEncargo.AreaPorEspacioDeNombres(p) != Marca(p))
                .Select(p => p.FullName).Should().BeEmpty(
                    "una petición en la carpeta de un área clasificada lleva esa misma área en la marca");
            marcadas.Should().OnlyContain(p => ActosExcluidosDelEncargo.AreaDe(p) == Marca(p), "la marca manda sobre la carpeta");
        }

        /// <summary>
        /// Movimiento simulado: la misma petición, con la misma marca, antes y después de M1. Con la regla anterior la
        /// excluida movida habría quedado abierta (área «Inbound»); con la de M0c da lo mismo en los dos sitios.
        /// </summary>
        [Fact]
        public void Mover_una_peticion_de_carpeta_no_cambia_si_queda_excluida()
        {
            var excluidaAntes = typeof(CaeManager.Application.AutorizacionesLectura.SimulacionM0c.PeticionExcluidaSimulada);
            var excluidaDespues = typeof(CaeManager.Application.Inbound.AutorizacionesLectura.SimulacionM0c.PeticionExcluidaSimulada);
            var permitidaAntes = typeof(CaeManager.Application.ContratosAcceso.SimulacionM0c.PeticionPermitidaSimulada);
            var permitidaDespues = typeof(CaeManager.Application.Inbound.ContratosAcceso.SimulacionM0c.PeticionPermitidaSimulada);

            // Control de sensibilidad: con la regla anterior (el espacio de nombres) la movida tendría área «Inbound»,
            // que no está excluida.
            ActosExcluidosDelEncargo.AreaPorEspacioDeNombres(excluidaDespues).Should().Be("Inbound");
            ActosExcluidosDelEncargo.AreasExcluidas.Should().NotContain("Inbound");

            ActosExcluidosDelEncargo.AreaDe(excluidaAntes).Should().Be("AutorizacionesLectura");
            ActosExcluidosDelEncargo.AreaDe(excluidaDespues).Should().Be("AutorizacionesLectura");
            ExcluyeSinInicializar(excluidaAntes).Should().BeTrue();
            ExcluyeSinInicializar(excluidaDespues).Should().BeTrue("moverla no la abre");

            ActosExcluidosDelEncargo.AreaDe(permitidaAntes).Should().Be("ContratosAcceso");
            ActosExcluidosDelEncargo.AreaDe(permitidaDespues).Should().Be("ContratosAcceso");
            ExcluyeSinInicializar(permitidaAntes).Should().BeFalse();
            ExcluyeSinInicializar(permitidaDespues).Should().BeFalse("moverla no la cierra");
        }

        /// <summary>
        /// Falla cerrado: una petición sin área resuelta queda excluida, nunca abierta en silencio. Además
        /// <c>AreasClasificadasParaElEncargoTests</c> pone en rojo el commit que la crea en Application.
        /// </summary>
        [Fact]
        public void Una_peticion_sin_area_resuelta_queda_excluida()
        {
            var sinMarcaAntes = typeof(CaeManager.Application.ContratosAcceso.SimulacionM0c.PeticionSinMarcaSimulada);
            var sinMarcaMovida = typeof(CaeManager.Application.Inbound.ContratosAcceso.SimulacionM0c.PeticionSinMarcaSimulada);

            ActosExcluidosDelEncargo.AreaDe(sinMarcaAntes).Should().Be("ContratosAcceso", "sin marca, el espacio de nombres");
            ExcluyeSinInicializar(sinMarcaAntes).Should().BeFalse();

            ActosExcluidosDelEncargo.AreaDe(sinMarcaMovida).Should().BeNull("«Inbound» es un módulo, no un área");
            ExcluyeSinInicializar(sinMarcaMovida).Should().BeTrue("sin área, falla cerrado");

            ActosExcluidosDelEncargo.AreaDe(typeof(PeticionConAreaInexistente)).Should().BeNull();
            ExcluyeSinInicializar(typeof(PeticionConAreaInexistente)).Should().BeTrue("una marca con un área no clasificada no abre nada");

            ActosExcluidosDelEncargo.AreaDe(typeof(PeticionFueraDeApplication)).Should().BeNull();
            ExcluyeSinInicializar(typeof(PeticionFueraDeApplication)).Should().BeTrue("fuera de la raíz y sin marca no hay área");
        }

        [Fact]
        public void Ningun_segmento_de_modulo_se_clasifica_como_area()
        {
            // «Facturacion» es a la vez módulo (D-M1) y área excluida entera: lo excluido no abre nada.
            var clasificadas = ActosExcluidosDelEncargo.AreasPermitidas.Concat(ActosExcluidosDelEncargo.AreasExcluidas).ToList();
            clasificadas.Should().NotContain(["Inbound", "Outbound", "Nucleo", "Composicion"],
                "clasificar un módulo como área abriría de golpe lo que se mueva a él (ADR-013 § 5, corrección A-3)");
        }

        /// <summary>
        /// Una petición que deriva de otra petición tiene la misma área resuelta que su base o queda excluida: nunca
        /// menos restringida que la base. Hoy puede no haber ninguna en Application; los tipos sintéticos de abajo
        /// prueban que la regla y este predicado distinguen los casos.
        /// </summary>
        [Fact]
        public void Toda_peticion_derivada_de_otra_tiene_el_area_de_su_base_o_queda_excluida()
        {
            var derivadas = TiposDeApplication
                .Where(t => t is { IsInterface: false, IsGenericTypeDefinition: false }
                            && typeof(IBaseRequest).IsAssignableFrom(t)
                            && t.BaseType is { } b && typeof(IBaseRequest).IsAssignableFrom(b))
                .ToList();

            derivadas.Where(p => !HeredaSinAbrirse(p)).Select(p => p.FullName).Should().BeEmpty(
                "una petición derivada no puede quedar menos restringida que su base");

            // Sensibilidad del predicado: una derivada de una base permitida que declara otra área permitida lo incumple.
            HeredaSinAbrirse(typeof(CaeManager.Application.Centros.SimulacionM0c.DerivadaConOtraAreaSimulada)).Should().BeFalse();
            HeredaSinAbrirse(typeof(CaeManager.Application.ContratosAcceso.SimulacionM0c.DerivadaDeExcluidaSimulada)).Should().BeTrue();
            HeredaSinAbrirse(typeof(CaeManager.Application.Inbound.ContratosAcceso.SimulacionM0c.DerivadaDePermitidaSimulada)).Should().BeTrue();
        }

        /// <summary>
        /// Hallazgo de Codex r1 (#2241): la marca de la base no se hereda sola (<c>Inherited = false</c>), y con la regla
        /// de c24529cfe una derivada de una excluida, declarada en un área permitida, quedaba permitida.
        /// </summary>
        [Fact]
        public void Una_peticion_derivada_de_una_excluida_queda_excluida_aunque_declare_un_area_permitida()
        {
            var baseExcluida = typeof(CaeManager.Application.AutorizacionesLectura.SimulacionM0c.PeticionBaseExcluidaSimulada);
            var derivada = typeof(CaeManager.Application.ContratosAcceso.SimulacionM0c.DerivadaDeExcluidaSimulada);
            var derivadaSinMarca = typeof(CaeManager.Application.ContratosAcceso.SimulacionM0c.DerivadaSinMarcaDeExcluidaSimulada);

            // Control de sensibilidad: con la regla de c24529cfe (marca propia, si no la carpeta) las dos derivadas tendrían
            // un área permitida.
            Marca(derivada).Should().Be("ContratosAcceso");
            Marca(derivadaSinMarca).Should().BeNull();
            ActosExcluidosDelEncargo.AreaPorEspacioDeNombres(derivadaSinMarca).Should().Be("ContratosAcceso");
            ActosExcluidosDelEncargo.AreasPermitidas.Should().Contain("ContratosAcceso");
            ExcluyeSinInicializar(baseExcluida).Should().BeTrue();

            ActosExcluidosDelEncargo.AreaDe(derivada).Should().Be("AutorizacionesLectura", "prevalece la exclusión de la base");
            ActosExcluidosDelEncargo.AreaDe(derivadaSinMarca).Should().Be("AutorizacionesLectura");
            ExcluyeSinInicializar(derivada).Should().BeTrue("una derivada nunca queda menos restringida que su base");
            ExcluyeSinInicializar(derivadaSinMarca).Should().BeTrue();

            // Base sin área resuelta: la derivada tampoco la tiene y falla cerrado.
            var derivadaDeSinArea = typeof(CaeManager.Application.ContratosAcceso.SimulacionM0c.DerivadaDeBaseSinAreaSimulada);
            Marca(derivadaDeSinArea).Should().Be("ContratosAcceso");
            ActosExcluidosDelEncargo.AreaDe(derivadaDeSinArea).Should().BeNull();
            ExcluyeSinInicializar(derivadaDeSinArea).Should().BeTrue();
        }

        /// <summary>
        /// Control positivo: heredar de una base permitida no excluye nada; sin marca propia, la derivada toma la marca más
        /// cercana de la cadena, así que moverla a <c>Inbound.*</c> no la deja sin área.
        /// </summary>
        [Fact]
        public void Una_peticion_derivada_de_una_permitida_sigue_permitida()
        {
            var derivada = typeof(CaeManager.Application.Inbound.ContratosAcceso.SimulacionM0c.DerivadaDePermitidaSimulada);

            Marca(derivada).Should().BeNull();
            ActosExcluidosDelEncargo.AreaPorEspacioDeNombres(derivada).Should().Be("Inbound");
            ActosExcluidosDelEncargo.AreaDe(derivada).Should().Be("ContratosAcceso", "la marca más cercana es la de la base");
            ExcluyeSinInicializar(derivada).Should().BeFalse();
        }

        /// <summary>
        /// <see cref="ActosExcluidosDelEncargo.PeticionesExcluidas"/> va por tipo: una derivada de una de ellas también queda
        /// excluida. Las de hoy son selladas o sin derivadas; lo que se comprueba es la cadena entera de cada petición.
        /// </summary>
        [Fact]
        public void Una_peticion_que_hereda_de_una_excluida_por_tipo_queda_excluida()
        {
            ActosExcluidosDelEncargo.PeticionesExcluidas.Should().HaveCountGreaterThan(15, "control positivo: la lista se ve");

            Peticiones
                .Where(p => Cadena(p).Any(ActosExcluidosDelEncargo.PeticionesExcluidas.Contains))
                .Where(p => !ExcluyeSinInicializar(p))
                .Select(p => p.FullName).Should().BeEmpty("ninguna derivada de una petición excluida por tipo queda abierta");

            // Sintética: deriva de una excluida por tipo (no sellada) y declara otra área permitida. Ni su área ni la de la
            // base están excluidas: solo la cadena de tipos la deja fuera. Con la comparación por tipo exacto quedaba abierta.
            var derivada = typeof(CaeManager.Application.ContratosAcceso.SimulacionM0c.DerivadaDeExcluidaPorTipoSimulada);
            var baseExcluida = typeof(CaeManager.Application.Integraciones.Commands.RepararMarcaHueco.RepararMarcaHuecoCommand);
            ActosExcluidosDelEncargo.PeticionesExcluidas.Should().Contain(baseExcluida).And.NotContain(derivada);
            ActosExcluidosDelEncargo.AreaDe(baseExcluida).Should().Be("Integraciones");
            ActosExcluidosDelEncargo.AreaDe(derivada).Should().Be("ContratosAcceso");
            ActosExcluidosDelEncargo.AreasExcluidas.Should().NotContain(["Integraciones", "ContratosAcceso"]);
            ExcluyeSinInicializar(derivada).Should().BeTrue("una derivada nunca queda menos restringida que su base");
        }

        /// <summary>
        /// Una petición genérica abierta no se instancia en el test del conjunto congelado; si alguna vez existe, su área se
        /// declara en la definición. Hoy no hay ninguna: el recuento lo fija para que la primera sea una decisión visible.
        /// </summary>
        [Fact]
        public void Ninguna_peticion_generica_abierta_queda_sin_marca()
        {
            var genericasAbiertas = TiposDeApplication
                .Where(t => t is { IsInterface: false, IsGenericTypeDefinition: true } && typeof(IBaseRequest).IsAssignableFrom(t))
                .ToList();

            genericasAbiertas.Where(t => !t.IsAbstract && Marca(t) is null).Select(t => t.FullName).Should().BeEmpty(
                "una petición genérica declara su área con [AreaDelEncargo(\"…\")] en la definición");
            genericasAbiertas.Select(t => t.FullName).Should().BeEmpty(
                "hoy no hay peticiones genéricas abiertas en Application; la primera se clasifica y se anota aquí");
        }

        private static IEnumerable<Type> Cadena(Type t)
        {
            for (var actual = t; actual is not null; actual = actual.BaseType)
                yield return actual;
        }

        /// <summary>
        /// Excluida por su tipo, sin instanciarla (vale también para una base abstracta): lo mismo que
        /// <see cref="ActosExcluidosDelEncargo.Excluye"/> salvo la variante por instancia del registro de auditoría.
        /// </summary>
        private static bool ExcluidaPorTipo(Type t) =>
            ActosExcluidosDelEncargo.AreaDe(t) is not { } area
            || ActosExcluidosDelEncargo.AreasExcluidas.Contains(area)
            || Cadena(t).Any(ActosExcluidosDelEncargo.PeticionesExcluidas.Contains);

        private static bool HeredaSinAbrirse(Type derivada) =>
            derivada.BaseType is not { } b
            || ActosExcluidosDelEncargo.AreaDe(derivada) == ActosExcluidosDelEncargo.AreaDe(b)
            || ExcluidaPorTipo(derivada);

        private static string? Marca(Type t) =>
            t.GetCustomAttributes(typeof(AreaDelEncargoAttribute), inherit: false).Cast<AreaDelEncargoAttribute>().SingleOrDefault()?.Area;

        [AreaDelEncargo("NoEsUnArea")]
        private sealed record PeticionConAreaInexistente : IRequest;

        private sealed record PeticionFueraDeApplication : IRequest;
    }
}

// Tipos sintéticos del movimiento simulado: la misma petición antes (carpeta de su área) y después de M1
// (CaeManager.Application.Inbound.*). Viven en el ensamblado de tests; los registros de Application y de módulos no los ven.
namespace CaeManager.Application.AutorizacionesLectura.SimulacionM0c
{
    [AreaDelEncargo("AutorizacionesLectura")]
    public sealed record PeticionExcluidaSimulada : IRequest;

    /// <summary>Base excluida por área, no sellada, para las derivadas sintéticas.</summary>
    [AreaDelEncargo("AutorizacionesLectura")]
    public record PeticionBaseExcluidaSimulada : IRequest;
}

namespace CaeManager.Application.SinAreaM0c.SimulacionM0c
{
    /// <summary>Base sin marca fuera de un área clasificada: sin área resuelta.</summary>
    public record PeticionBaseSinAreaSimulada : IRequest;
}

namespace CaeManager.Application.Centros.SimulacionM0c
{
    /// <summary>Incumple la regla de herencia: base permitida en «ContratosAcceso», derivada en otra área permitida.</summary>
    [AreaDelEncargo("Centros")]
    public sealed record DerivadaConOtraAreaSimulada : CaeManager.Application.ContratosAcceso.SimulacionM0c.PeticionBasePermitidaSimulada;
}

namespace CaeManager.Application.Inbound.AutorizacionesLectura.SimulacionM0c
{
    [AreaDelEncargo("AutorizacionesLectura")]
    public sealed record PeticionExcluidaSimulada : IRequest;
}

namespace CaeManager.Application.ContratosAcceso.SimulacionM0c
{
    [AreaDelEncargo("ContratosAcceso")]
    public sealed record PeticionPermitidaSimulada : IRequest;

    public sealed record PeticionSinMarcaSimulada : IRequest;

    /// <summary>Base permitida, no sellada, para las derivadas sintéticas.</summary>
    [AreaDelEncargo("ContratosAcceso")]
    public record PeticionBasePermitidaSimulada : IRequest;

    // Hallazgo de Codex r1 (#2241): derivadas de una excluida declaradas en un área permitida.
    [AreaDelEncargo("ContratosAcceso")]
    public sealed record DerivadaDeExcluidaSimulada : CaeManager.Application.AutorizacionesLectura.SimulacionM0c.PeticionBaseExcluidaSimulada;

    public sealed record DerivadaSinMarcaDeExcluidaSimulada : CaeManager.Application.AutorizacionesLectura.SimulacionM0c.PeticionBaseExcluidaSimulada;

    [AreaDelEncargo("ContratosAcceso")]
    public sealed record DerivadaDeBaseSinAreaSimulada : CaeManager.Application.SinAreaM0c.SimulacionM0c.PeticionBaseSinAreaSimulada;

    [AreaDelEncargo("ContratosAcceso")]
    public sealed record DerivadaDeExcluidaPorTipoSimulada()
        : CaeManager.Application.Integraciones.Commands.RepararMarcaHueco.RepararMarcaHuecoCommand(Guid.Empty);
}

namespace CaeManager.Application.Inbound.ContratosAcceso.SimulacionM0c
{
    [AreaDelEncargo("ContratosAcceso")]
    public sealed record PeticionPermitidaSimulada : IRequest;

    public sealed record PeticionSinMarcaSimulada : IRequest;

    /// <summary>Control positivo: sin marca propia, en «Inbound», hereda de una permitida y toma su marca.</summary>
    public sealed record DerivadaDePermitidaSimulada : CaeManager.Application.ContratosAcceso.SimulacionM0c.PeticionBasePermitidaSimulada;
}
