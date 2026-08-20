import QtQuick
import QtQuick.Controls as QQC2
import org.kde.kirigami as Kirigami
import org.kde.kcmutils as KCM

KCM.SimpleKCM {
    property alias cfg_endpoint: endpointField.text
    property alias cfg_refreshInterval: refreshSpin.value
    property alias cfg_maximumEvents: eventSpin.value

    Kirigami.FormLayout {
        QQC2.TextField {
            id: endpointField
            Kirigami.FormData.label: i18n("Agenda endpoint:")
            inputMethodHints: Qt.ImhUrlCharactersOnly
        }
        QQC2.SpinBox {
            id: refreshSpin
            Kirigami.FormData.label: i18n("Refresh every:")
            from: 10
            to: 300
            textFromValue: value => i18np("%1 second", "%1 seconds", value)
        }
        QQC2.SpinBox {
            id: eventSpin
            Kirigami.FormData.label: i18n("Events shown:")
            from: 1
            to: 20
        }
    }
}
