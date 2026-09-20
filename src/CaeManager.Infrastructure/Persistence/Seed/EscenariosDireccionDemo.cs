namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// El estado de acceso de un Cliente empresarial visto por quien gestiona su
/// rama — la matriz que la demo a dirección tiene que poder enseñar entera.
///
/// <para>
/// <b>Lectura del vocabulario, no concepto nuevo.</b> El dominio no tiene un
/// estado «acceso» por Cliente empresarial: lo que existe, y lo que pinta la
/// pantalla, es (a) el <c>EstadoCentro</c> que calcula
/// <c>CalculoEstadoCentroService</c> (<c>Bloqueado</c> = «acceso bloqueado»,
/// <c>Vigente</c> = «con acceso y sin documentación pendiente»,
/// <c>Proximo/Urgente/Vencido/Faltante</c> = «con acceso pero con documentación
/// pendiente») y (b) el <c>EstadoAcreditacion</c> de cada documento ante la
/// plataforma del Cliente empresarial (<c>Subida</c> = «pendiente de
/// confirmación»). Cada valor de este enum se define por el estado real que
/// el sembrador deja medible con esos mismos servicios; ver
/// <c>EscenariosDireccionDemoTests</c>, que lo comprueba contra ellos y no
/// contra una réplica.
/// </para>
///
/// <para>
/// «Completo» y «casi completo» son la única interpretación que no sale del
/// código: aquí, «completo» = todo al día y con todas las acreditaciones
/// aceptadas, y «casi completo» = igual salvo un único documento a punto de
/// vencer. Está anotado como duda para el propietario en el informe de la
/// fase 1: si «completo» significa otra cosa (p. ej. completitud de la ficha),
/// se cambia aquí y en la prueba, no en el modelo.
/// </para>
/// </summary>
public enum EscenarioClienteDemo
{
    /// <summary>Todos los centros <c>Vigente</c> y todas las acreditaciones aceptadas, con la ficha entera rellenada (contacto y fin de contrato en los tres centros).</summary>
    Completo,

    /// <summary>Como <see cref="Completo"/>, salvo exactamente un centro <c>Proximo</c> (un trabajador y un permiso de extranjero a punto de vencer).</summary>
    CasiCompleto,

    /// <summary>Sin ningún centro bloqueado, pero con un centro <c>Vencido</c>, uno <c>Urgente</c> y uno <c>Faltante</c>, y acreditaciones sin subir.</summary>
    ConAccesoConDocumentacionPendiente,

    /// <summary>Todos los centros <c>Vigente</c> y todas las acreditaciones aceptadas, pero con la ficha a medias (centros sin contacto ni fin de contrato).</summary>
    ConAccesoSinDocumentacionPendiente,

    /// <summary>Al menos un centro <c>Bloqueado</c> (requisito bloqueante sin cumplir por un extranjero sin documento de identidad presentado).</summary>
    AccesoBloqueado,

    /// <summary>Centros <c>Vigente</c>, pero ninguna acreditación aceptada: todas subidas y a la espera de la plataforma del cliente.</summary>
    AccesoPendienteDeConfirmacion
}

/// <summary>Qué Gestor CAE del Operador CAE externo responde de un Cliente empresarial (su Asignación de Cartera).</summary>
public enum GestorDemo
{
    Primero,
    Segundo
}

/// <param name="RazonSocial">Del Cliente empresarial. Ficticio, y distinto de los nueve de <c>DatosPruebaSeeder</c> para no chocar en un mismo Tenant.</param>
/// <param name="Contratista">Empresa que opera sus centros y aporta la plantilla.</param>
/// <param name="Subcontrata">Subcontrata de la contratista, con plantilla propia en dos de los centros.</param>
public sealed record ClienteEscenarioDemo(
    string RazonSocial, string Contratista, string Subcontrata, EscenarioClienteDemo Escenario, GestorDemo Gestor);

/// <summary>La rama de un Tenant propietario: los Clientes empresariales que se le siembran.</summary>
public sealed record RamaEscenariosDemo(string NombreTenant, IReadOnlyList<ClienteEscenarioDemo> Clientes);

/// <summary>
/// Reparto de la matriz entre los seis Tenants propietarios que opera el
/// Operador CAE externo de la demo (ArcoSPA). Cada estado de
/// <see cref="EscenarioClienteDemo"/> aparece al menos una vez; el primer
/// Gestor CAE lleva un Cliente empresarial en cada uno de los seis Tenants y
/// el segundo, uno en tres de ellos, de modo que el Coordinador CAE tiene dos
/// carteras que ver.
///
/// <para>
/// Nombres de ficción, como toda la siembra de demo: ninguna razón social
/// puede coincidir con una compañía real. Ni aquí ni en ningún test aparece el
/// nombre del cliente fundador ni el de sus clientes (reserva del propietario,
/// 2026-09-20).
/// </para>
/// </summary>
public static class CatalogoEscenariosDireccionDemo
{
    public const string NombreTenantDuff = "Cervezas Duff S.A.";
    public const string NombreTenantPizzaPlanet = "Pizza Planet S.L.";

    public static readonly RamaEscenariosDemo Refrielectric = new(
        DelegacionDemoSeeder.NombreTenantRefrielectric,
    [
        new("Hoteles Grand Budapest S.A.", "Montajes Frigoríficos Zubrowka S.L.", "Servicios Auxiliares Lobby S.L.",
            EscenarioClienteDemo.Completo, GestorDemo.Primero),
    ]);

    public static readonly RamaEscenariosDemo Dexter = new(
        DelegacionDemoSeeder.NombreTenantClienteDemo,
    [
        new("Wonka Industrias S.A.", "Instalaciones Oompa S.L.", "Envases Loompa S.L.",
            EscenarioClienteDemo.CasiCompleto, GestorDemo.Primero),
        new("Initech Iberia S.L.", "Mantenimiento Milton S.L.", "Archivos Swingline S.L.",
            EscenarioClienteDemo.AccesoBloqueado, GestorDemo.Segundo),
    ]);

    public static readonly RamaEscenariosDemo PlanetExpress = new(
        DelegacionDemoSeeder.NombreTenantClienteDemo2,
    [
        new("Oceanic Airlines España S.A.", "Servicios de Pista Nostromo S.L.", "Catering Dharma S.L.",
            EscenarioClienteDemo.ConAccesoSinDocumentacionPendiente, GestorDemo.Primero),
        new("Gringotts Banca Mágica S.A.", "Seguridad Hogwarts S.L.", "Custodia Bóveda S.L.",
            EscenarioClienteDemo.AccesoPendienteDeConfirmacion, GestorDemo.Segundo),
    ]);

    public static readonly RamaEscenariosDemo KrustyKrab = new(
        DelegacionDemoSeeder.NombreTenantClienteDemo3,
    [
        new("Cadena Los Pollos Hermanos S.L.", "Logística Albuquerque S.L.", "Frío Industrial Verde S.L.",
            EscenarioClienteDemo.ConAccesoConDocumentacionPendiente, GestorDemo.Primero),
    ]);

    public static readonly RamaEscenariosDemo Duff = new(
        NombreTenantDuff,
    [
        new("Tyrell Robótica S.L.", "Electricidad Nexus S.L.", "Vigilancia Replicante S.L.",
            EscenarioClienteDemo.AccesoPendienteDeConfirmacion, GestorDemo.Primero),
    ]);

    public static readonly RamaEscenariosDemo PizzaPlanet = new(
        NombreTenantPizzaPlanet,
    [
        new("Umbrella Corporation Ibérica S.A.", "Limpiezas Raccoon S.L.", "Laboratorios Nemesis S.L.",
            EscenarioClienteDemo.AccesoBloqueado, GestorDemo.Primero),
        new("Cyberdyne Ibérica S.A.", "Montajes Skynet S.L.", "Transportes Terminator S.L.",
            EscenarioClienteDemo.CasiCompleto, GestorDemo.Segundo),
    ]);

    /// <summary>Las seis ramas, en el orden de los Tenants de la demo.</summary>
    public static IReadOnlyList<RamaEscenariosDemo> Ramas { get; } =
        [Refrielectric, Dexter, PlanetExpress, KrustyKrab, Duff, PizzaPlanet];
}

/// <summary>
/// Cuántos días desde hoy tiene el vencimiento de cada <see cref="VigenciaDemo"/>.
/// Elegidos para <b>sobrevivir unos días</b> a la fecha de siembra sin cambiar
/// de estado (la demo puede ser al día siguiente o tres después): con los
/// umbrales de <c>ParametroSistemaSeedData</c> (ámbar 30, rojo 15), «a punto
/// de vencer» aguanta ~12 días antes de ser urgente, «urgente» ~10 antes de
/// vencer y «vencido» ya no puede mejorar. <c>EscenariosDireccionDemoTests</c>
/// fija que cada valor cae en su banda con esos umbrales, y con margen.
/// </summary>
public enum VigenciaDemo
{
    AlDia,
    APuntoDeVencer,
    Urgente,
    Vencida
}

public static class VigenciaDemoExtensiones
{
    public static int DiasHastaVencimiento(this VigenciaDemo vigencia) => vigencia switch
    {
        VigenciaDemo.AlDia => 200,
        VigenciaDemo.APuntoDeVencer => 27,
        VigenciaDemo.Urgente => 10,
        VigenciaDemo.Vencida => -20,
        _ => throw new ArgumentOutOfRangeException(nameof(vigencia), vigencia, null)
    };
}
