using System.Linq.Expressions;
using CaeManager.Domain.Common;

namespace CaeManager.Domain.ContratosAcceso;

/// <summary>
/// Contrato de acceso (Inbound M1): la relación Empresa contratista × Centro de trabajo × periodo que da a esa
/// Empresa contratista acceso al Centro. Vive en el Tenant propietario del Cliente empresarial —el titular del
/// Centro—, no en el de la Empresa contratista: <see cref="EmpresaContratistaId"/> es la Empresa contraparte local
/// con la que ese Tenant representa a la contratista (modelo egocéntrico por Tenant), nunca el Tenant de la
/// contratista.
///
/// <para>
/// <b>Los requisitos no se guardan aquí.</b> Lo que se exige a la Empresa contratista mientras el contrato está
/// vigente son las filas <c>TipoDocumentoCentro</c> del Centro (opción 1 de DI-04): un contrato no copia ni
/// congela requisitos, y cambiar los del Centro cambia lo que se pide a todos sus contratos vigentes. Por eso
/// tampoco tiene nombre propio: se identifica por sus tres coordenadas y su periodo.
/// </para>
///
/// <para>
/// <see cref="ClienteEmpresarialId"/> es la Empresa titular del Centro y tiene que coincidir con <c>Centro.ClienteId</c>: la
/// FK compuesta (TenantId, CentroId, ClienteEmpresarialId) contra la clave alternativa (TenantId, Id, ClienteId) de Centro lo hace irrepresentable en la
/// base, igual que en <see cref="Proyectos.Proyecto"/>. Que la Empresa contratista no sea el propio Cliente
/// empresarial es invariante de dominio (<see cref="Crear"/>). Se llama <c>ClienteEmpresarialId</c> y no
/// <c>ClienteId</c> como las tablas nuevas de Plataforma CAE y de tolerancias por Cliente empresarial:
/// <c>ClienteIdNoSeExtiendeTests</c> congela la columna legacy <c>ClienteId</c> mientras la decisión D1 siga abierta.
/// </para>
///
/// <para>
/// Igual que <see cref="Proyectos.Proyecto"/>, el estado abierto/cerrado no se guarda como enum: se deriva de
/// <see cref="FechaCierreReal"/>. <see cref="FechaFin"/> es el fin contratado (null = indefinido) y
/// <see cref="FechaCierreReal"/> el cierre real, que puede adelantarse a ese fin. El criterio de vigencia en un día
/// vive en <see cref="EstaVigenteEn"/>, el mismo que traducen a SQL las consultas.
/// </para>
///
/// <para>
/// <b>Solapes permitidos.</b> Dos contratos de la misma Empresa contratista en el mismo Centro pueden solaparse en el
/// tiempo: no hay regla de unicidad ni índice único. Es el valor por defecto mientras DN-6 sigue abierta, el mismo
/// que tiene Proyecto (varios Proyectos abiertos en un Centro); si DN-6 decide lo contrario, la regla entra aquí y en
/// <c>CrearContratoAccesoCommand</c>.
/// </para>
/// </summary>
public class ContratoAcceso : EntidadBase
{
    public const int LongitudMaximaNotas = 1000;

    public Guid ClienteEmpresarialId { get; private set; }
    public Guid CentroId { get; private set; }
    public Guid EmpresaContratistaId { get; private set; }
    public DateOnly FechaInicio { get; private set; }
    public DateOnly? FechaFin { get; private set; }
    public DateOnly? FechaCierreReal { get; private set; }
    public string? Notas { get; private set; }

    public bool EstaAbierto => FechaCierreReal is null;

    private ContratoAcceso()
    {
    }

    public static ContratoAcceso Crear(
        Guid clienteEmpresarialId, Guid centroId, Guid empresaContratistaId, DateOnly fechaInicio, DateOnly? fechaFin, string? notas)
    {
        if (clienteEmpresarialId == Guid.Empty)
            throw new ArgumentException("El contrato de acceso debe pertenecer a un Cliente empresarial.", nameof(clienteEmpresarialId));
        if (centroId == Guid.Empty)
            throw new ArgumentException("El contrato de acceso debe pertenecer a un Centro de trabajo.", nameof(centroId));
        if (empresaContratistaId == Guid.Empty)
            throw new ArgumentException("El contrato de acceso debe tener una Empresa contratista.", nameof(empresaContratistaId));
        if (empresaContratistaId == clienteEmpresarialId)
            throw new ArgumentException(
                "La Empresa contratista no puede ser el propio Cliente empresarial titular del Centro.", nameof(empresaContratistaId));
        ExigirFinNoAnteriorAlInicio(fechaInicio, fechaFin);

        var contrato = new ContratoAcceso
        {
            ClienteEmpresarialId = clienteEmpresarialId,
            CentroId = centroId,
            EmpresaContratistaId = empresaContratistaId,
            FechaInicio = fechaInicio,
            FechaFin = fechaFin
        };
        contrato.EstablecerNotas(notas);
        return contrato;
    }

    /// <summary>Las coordenadas (Cliente empresarial, Centro, Empresa contratista) y el inicio no cambian: otro periodo u otra Empresa es otro contrato.</summary>
    public void Actualizar(DateOnly? fechaFin, string? notas)
    {
        ExigirFinNoAnteriorAlInicio(FechaInicio, fechaFin);

        FechaFin = fechaFin;
        EstablecerNotas(notas);
    }

    public void Cerrar(DateOnly fechaCierre)
    {
        if (!EstaAbierto)
            throw new InvalidOperationException("El contrato de acceso ya está cerrado.");
        if (fechaCierre < FechaInicio)
            throw new ArgumentException("La fecha de cierre no puede ser anterior a la de inicio.", nameof(fechaCierre));

        FechaCierreReal = fechaCierre;
    }

    /// <summary>Deshace un cierre: el contrato vuelve a contar como abierto con su fin contratado de antes.</summary>
    public void Reabrir()
    {
        if (EstaAbierto)
            throw new InvalidOperationException("El contrato de acceso no está cerrado.");

        FechaCierreReal = null;
    }

    /// <summary>
    /// ¿Da acceso el contrato en <paramref name="fecha"/>? No eliminado, ya empezado, sin pasar su fin contratado y sin
    /// pasar su cierre real; el día de inicio, el de fin y el de cierre cuentan como vigentes. Es la versión en memoria
    /// de <see cref="EstaVigenteEn"/>: un test de dominio comprueba que las dos coinciden en cada borde.
    /// </summary>
    public bool VigenteEn(DateOnly fecha) =>
        !EstaEliminado
        && FechaInicio <= fecha
        && (FechaFin == null || fecha <= FechaFin)
        && (FechaCierreReal == null || fecha <= FechaCierreReal);

    /// <summary>
    /// El criterio de <see cref="VigenteEn"/> como expresión traducible a SQL, para las consultas que filtran contratos
    /// vigentes en un día sin cargarlos.
    /// </summary>
    public static Expression<Func<ContratoAcceso, bool>> EstaVigenteEn(DateOnly fecha) =>
        c => !c.EstaEliminado
             && c.FechaInicio <= fecha
             && (c.FechaFin == null || fecha <= c.FechaFin)
             && (c.FechaCierreReal == null || fecha <= c.FechaCierreReal);

    private static void ExigirFinNoAnteriorAlInicio(DateOnly fechaInicio, DateOnly? fechaFin)
    {
        if (fechaFin is not null && fechaFin < fechaInicio)
            throw new ArgumentException("La fecha de fin no puede ser anterior a la de inicio.", nameof(fechaFin));
    }

    private void EstablecerNotas(string? notas)
    {
        if (notas?.Length > LongitudMaximaNotas)
            throw new ArgumentException($"Las notas no pueden superar {LongitudMaximaNotas} caracteres.", nameof(notas));

        Notas = notas;
    }
}
