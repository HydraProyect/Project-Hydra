using CaeManager.Domain.Common;

namespace CaeManager.Domain.Documentos;

/// <summary>
/// Override de nivel 2 (Gestor CAE) sobre la lectura automática por IA de un
/// TipoDocumento, para un Cliente concreto de su cartera —
/// <see cref="TipoDocumento.LecturaIaActiva"/> es el nivel 1 (Administrador,
/// global). Fila dispersa: sin fila para un (Cliente, TipoDocumento) dado,
/// el estado efectivo es simplemente el del nivel 1 — no hace falta una fila
/// por cada combinación posible. Al reasignar Cliente.EjecutivoUsuarioId a
/// otro Gestor, esta fila se conserva tal cual (está indexada por
/// ClienteId, no por el Gestor que la creó), que es justo el comportamiento
/// pedido: el Cliente mantiene su configuración anterior, y es
/// responsabilidad del nuevo Gestor revisarla (ver el aviso que dispara
/// ReasignarEjecutivoClienteCommand).
/// </summary>
public class ConfiguracionIaDocumentoCliente : EntidadConTenant
{
    public Guid ClienteId { get; private set; }
    public Guid TipoDocumentoId { get; private set; }
    public bool Activa { get; private set; }

    private ConfiguracionIaDocumentoCliente()
    {
    }

    public ConfiguracionIaDocumentoCliente(Guid clienteId, Guid tipoDocumentoId, bool activa)
    {
        ClienteId = clienteId;
        TipoDocumentoId = tipoDocumentoId;
        Activa = activa;
    }

    public void Establecer(bool activa) => Activa = activa;
}
