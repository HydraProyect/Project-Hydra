using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Acreditacion;

/// <summary>
/// Conciliación de la regla A-1 sobre lo que ya está guardado: crea las acreditaciones de plataforma que falten
/// para los Documentos de Empresa operativos del Tenant en curso, con la misma regla que las altas
/// (<see cref="IAltaAcreditacionesPlataformaService"/>, <see cref="AltasConAcreditacion.CentrosAConciliar"/>). Cada
/// Centro con acceso de plataforma se evalúa entero: un Documento de Empresa queda acreditado ante cada acceso de
/// plataforma de cada Centro de su Empresa (o donde trabaja alguien de ella) que exige su tipo; un Centro sin gestión
/// CAE queda fuera, igual que en el alta.
///
/// <para>
/// Cubre lo que el alta no alcanza: los datos anteriores a la regla A-1, los Documentos de Empresa creados por una
/// instancia anterior durante el relevo azul/verde, y el par que dos altas simultáneas dejaron sin crear porque ninguna
/// veía lo que la otra aún no había confirmado, por un camino que no se serializa: el alta de un Documento de Empresa
/// y la de un acceso de plataforma sí se serializan (<see cref="ISerializacionAltasAcreditacionEmpresa"/>).
/// </para>
///
/// <para>
/// Idempotente: el alta no agrega un par que ya existe, y un choque con el índice único durante el guardado se trata
/// como «ya existe». Solo agrega; nunca da de baja ni modifica una acreditación existente. No repara los Documentos
/// de Trabajador, cuya regla de alta no cambió con A-1.
/// </para>
///
/// <para>
/// Un solo Tenant cada vez: lee y escribe con los contextos del Tenant en curso, bajo la RLS normal, sin
/// <c>IgnoreQueryFilters</c> ni identidad que se salte la RLS. Quien la invoca abre el ámbito de cada Tenant
/// propietario (<c>ConciliacionAcreditacionesEmpresaHostedService</c>). No se ejecuta sola: el disparador está
/// apagado por defecto y lo enciende quien despliega.
/// </para>
/// </summary>
public interface IConciliacionAcreditacionesEmpresaService
{
    /// <summary>
    /// Una pasada sobre los Centros con acceso de plataforma del Tenant en curso, por lotes. Un lote que falla se
    /// descarta entero y no impide los siguientes: el resultado lo cuenta y conserva su error, para que quien la invoca
    /// no la dé por terminada y la repita.
    /// </summary>
    Task<ResultadoConciliacionTenant> ConciliarTenantEnCursoAsync(CancellationToken cancellationToken = default);
}

/// <summary>Lo que dejó una pasada de la conciliación en un Tenant propietario.</summary>
/// <param name="Creadas">
/// Acreditaciones que la pasada agregó en lotes que se guardaron. Es una cota superior, solo para el registro: si un alta
/// simultánea guardó el mismo par antes que el lote, el guardado lo trata como «ya existe» y no lo inserta, pero aquí
/// sigue contado (revisión Codex de la PR #2074, ronda 6). Nada decide con este número.
/// </param>
/// <param name="Lotes">Lotes de Centros recorridos.</param>
/// <param name="Errores">Un error por cada lote que falló; ese lote no guardó nada.</param>
public sealed record ResultadoConciliacionTenant(int Creadas, int Lotes, IReadOnlyList<Exception> Errores)
{
    public int LotesFallidos => Errores.Count;

    /// <summary>Verdadero si todos los lotes se guardaron (o no tenían nada que crear).</summary>
    public bool Completa => Errores.Count == 0;
}

public class ConciliacionAcreditacionesEmpresaService(
    ICentrosQueryContext centrosContext,
    IAltaAcreditacionesPlataformaService altaAcreditaciones,
    IUnitOfWork unitOfWork,
    IDescarteCambiosPendientes descarte)
    : IConciliacionAcreditacionesEmpresaService
{
    /// <summary>
    /// Centros por guardado: acota el tamaño de las listas de la consulta en un Tenant con muchos Centros. Cada lote se
    /// guarda entero o nada; uno ya guardado no se repite en la pasada siguiente.
    /// </summary>
    internal const int CentrosPorLote = 100;

    public async Task<ResultadoConciliacionTenant> ConciliarTenantEnCursoAsync(CancellationToken cancellationToken = default)
    {
        // Solo el identificador del Centro: el acceso guarda credenciales cifradas que aquí no hacen falta.
        var centrosConPlataforma = await centrosContext.CanalesGestionDocumental
            .Where(c => c.Tipo == TipoCanalGestion.Plataforma)
            .Select(c => c.CentroId)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);

        var creadas = 0;
        var lotes = 0;
        var errores = new List<Exception>();
        foreach (var lote in centrosConPlataforma.Chunk(CentrosPorLote))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lotes++;

            try
            {
                var agregadas = await altaAcreditaciones.AgregarPendientesAsync(
                    new AltasConAcreditacion { CentrosAConciliar = lote, SoloDocumentosDeEmpresa = true }, cancellationToken);
                if (agregadas > 0)
                {
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                    creadas += agregadas;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // El lote fallido no se arrastra al siguiente guardado: sin descartarlo, el lote siguiente volvería a
                // intentar guardar lo mismo y fallaría con él.
                errores.Add(ex);
            }
            finally
            {
                // Lo ya guardado tampoco se queda rastreado: la memoria del contexto no crece con los lotes del Tenant.
                descarte.DescartarCambiosPendientes();
            }
        }

        return new ResultadoConciliacionTenant(creadas, lotes, errores);
    }
}