# AWS検証サーバーの配置と検証

このフォルダーは、専用サーバーのAWS配置用です。新たに配置するときは、料金と構成についてユーザーの明示的な承認を受けてからCloudFormationを適用します。

## 対象プロジェクトと配置前の確認結果（2026年11月22日）

会社用プロファイル `feel-development`。選択リージョンはシドニー（`ap-southeast-2`）。AWS Settings の「View all projects → Overview → Additional Info → Region」で確認できます。AWS CLI の読み取りでは無料プランが有効で、残りクレジットは US$100。シドニーにはデフォルトVPCが1つあり、EC2、EBS、Elastic IP、EC2キーペア、有効なCloudFormationスタックは0件でした。

## 初回検証の構成

- Ubuntu Server 22.04 LTS、x86_64 の `c7i-flex.large`（2 vCPU、4 GiB）1台。シドニー3 AZで提供され、無料プラン対象として確認済み。試合中のメモリ、CPU、応答時間は実測します。
- 暗号化した gp3 20 GiB、専用VPC・公開サブネット・Internet Gateway、自動割り当てのパブリックIPv4 1個。
- UDP 7777 は参加者のグローバルIPv4 /32 最大5件、TCP 22 は管理者の /32 のみ。管理者も参加する場合は両方に登録します。SSHにはシドニーのEC2キーペアが必要です。
- サーバープロセスは非rootのsystemdサービスで起動。ローカルログとログローテーションを使います。
- 初回は起動から120分で自動停止。EC2の停止状態を確認し、検証後のEBS保持または削除は承認時に決めます。
- EC2、EBS、VPC、CloudFormation、Systems Manager は[無料プランのサポート対象](https://docs.aws.amazon.com/accounts/latest/reference/supported-services-sign-up-new.html)です。テンプレートの作成・接続試験は承認後に行います。

## シドニーでの料金概算

AWS Price List の Linux オンデマンドは US$0.11072/時間、gp3 は US$0.096/GiB月。[パブリックIPv4](https://aws.amazon.com/vpc/pricing/)は US$0.005/時間です。税・転送・クレジット適用前で、2時間後にEBSも削除すると約 **US$0.24**、2時間稼働後にEBSを1か月保持すると約 **US$2.15**、月10時間稼働でEBSを1か月保持すると約 **US$3.08** です。実請求ではEBSの実際の保持時間を使います。[インターネット向けデータ転送](https://aws.amazon.com/ec2/pricing/on-demand/)はプロジェクト全体の無料枠消費状況と実測値を確認します。

`Estimate-Cost.ps1` に `-Ec2UsdPerHour 0.11072 -Gp3UsdPerGbMonth 0.096` を渡すと、AWS接続なしで再計算できます。停止後もディスクを保持する場合は `-DiskRetainedHours` を指定します。

## 先に行うローカルの作業

1. Unity Hubで現在のEditorに **Linux Dedicated Server Build Support** を追加します。Unity 6000.3.27f2 には追加済みです。
2. UnityでSampleSceneを保存し、**Fuchu Walk → Local Server → Build Linux Server (AWS)** を実行します。Windows用・クライアント用と混ぜず、新しい出力フォルダー（例：Builds/LinuxServer）を選びます。
3. Linux x86_64のDedicated Serverを、サーバー専用シーンだけでビルドします。`server-manifest.json` にプラットフォーム・Unity版・ゲーム版・プロトコル5を記録します。バッチモードでは、生成済みのサーバー専用シーンを使用し、`FUCHU_LINUX_BUILD_DIRECTORY` に空の出力先の絶対パスを指定して `-buildTarget Linux64 -standaloneBuildSubtarget Server -executeMethod FuchuLocalServerBuild.BuildLinuxServerBatch` を実行します。
4. Ubuntu 22.04のローカル環境がある場合は、バンドルをコピーし、`chmod +x FuchuServer.x86_64 UnityCrashHandler64`（後者が存在する場合）を行い、`bash StartLocalServer.sh` で起動してWindowsクライアントから確認します。別PCからは環境変数 `FUCHU_BIND=0.0.0.0` を設定して起動します。WSL2を利用する場合はそのネットワーク設定に応じて接続先が変わります。こちらではWSLやLinux VMを作成・起動していません。
5. 配布用アーカイブをローカルで作ります。次はPowerShellの例です（プロジェクトルートで実行）。

```powershell
.\Deployment\Aws\Package-Server.ps1 -BuildDirectory .\Builds\LinuxServer -OutputArchive .\Builds\fuchu-linux-01.tar.gz
```

Windows標準のtarを使用します。Linuxビルドであることをマニフェストで確認し、運用ファイルを `deployment/` に同梱、全ファイルのSHA256一覧とアーカイブのSHA256ファイルを作成します。アップロード・クラウド接続は行いません。実行権限はLinux側のインストールで設定し、シェルスクリプトはLFに揃えます。出力済みアーカイブは上書きしません。

2026年11月22日、Unity 6000.3.27f2 の Dedicated Server 版を `Builds/fuchu-linux-sydney-02.tar.gz` に梱包しました。Ubuntu 22.04（x86_64）のローカルコンテナで、アーカイブと同梱ファイルのSHA-256、非root起動、UDP 7777の待受、起動ログのプロトコル5・5枠・NPC 5体を確認しました。クライアント接続とEC2上のsystemd起動は未検証です。

`fuchu-linux-sydney-01.tar.gz` は通常Playerで起動時クラッシュを再現した診断用の成果物です。配置対象には `fuchu-linux-sydney-02.tar.gz` と対応する `.sha256` を使用します。

## 承認を受けた後のAWS作業

以下は承認後の手順書であり、今回実行した内容ではありません。

1. シドニーで使用するキーペア名と、管理者および各プレイヤーのグローバルIPv4 /32を確認します。キーペアの登録は承認後に行います。秘密鍵はローカルPCで管理し、チャットやテンプレートに貼り付けません。
2. AWS ConsoleのCloudFormationで `ec2-test.template.json` を選び、パラメーターと料金をレビューして作成します。Ubuntu AMIはCanonicalの公開SSMパラメーターから解決します。VPCとルートも作るため、既存のdefault VPCには依存しません。
3. EC2の起動を確認します。Ubuntuの初期設定は `sudo cloud-init status --wait`、完了マーカーは `/var/log/fuchu-bootstrap-ready` です。OS停止タイマーは依存パッケージのインストールより先に登録します。初期設定だけではゲームを自動ダウンロード・起動しません。
4. SSHで `mkdir -p ~/fuchu-upload` を行い、SCPでアーカイブと `.sha256` をそのディレクトリーへ送ります。SSHユーザーはubuntuです。
5. SSH接続後の操作例：

```bash
cd ~/fuchu-upload
sha256sum --check fuchu-linux-01.tar.gz.sha256
mkdir -p unpacked-01
tar -xzf fuchu-linux-01.tar.gz -C unpacked-01
sudo bash unpacked-01/deployment/install-server.sh
```

6. インストーラーはファイル単位のSHA256も確認し、新しい `/opt/fuchu/releases/<UTC時刻>` に配置します。既存リリースは削除せず、`current` シンボリックリンクを切り替えます。実行ユーザーは非rootのfuchuです。更新時は進行中の試合が切断されるため、全員退出後に行います。
7. `server.env` は既存の設定を上書きしません。既定は0.0.0.0:7777です。`systemctl enable --now` で起動し、次のコマンドで確認します。

```bash
sudo systemctl status fuchu-server --no-pager
sudo tail -n 50 /var/log/fuchu/server.log
sudo journalctl -u fuchu-server --no-pager -n 50
sudo ss -lunp | grep ':7777'
systemctl list-timers fuchu-host-stop.timer
free -h
```

8. Windowsクライアントの **マルチプレイ → 専用サーバー（IP接続）**（旧名称：ローカル専用サーバー）で、EC2の現在のパブリックIPv4と7777を入力します。管理プレイヤーが試合を開始し、3人で領域・NPC・接触・タイマーを確認します。参加者のIPv4が変わった場合は許可設定の更新が必要です。
9. systemdは異常終了時のみ再起動し、繰り返す場合は起動制限で停止します。ゲームの終了ではプロセスを自動再起動しません。1プロセス1試合のため、再戦は全員退出後に `sudo systemctl restart fuchu-server` を行います。旧プロセスの領域・試合は復元されません。

## 停止と後片付け

- **ゲームだけ停止**：`sudo systemctl stop fuchu-server`。EC2は稼働し続けるので、EC2の料金は止まりません。
- **検証を中断**：AWS ConsoleでEC2を停止し、Stoppedを確認。EBSは残り、保存費用が続きます。
- **検証終了**：必要なログを取得し、ユーザーが承認した範囲でCloudFormationスタックを削除。インスタンス・ルートEBS・専用ネットワークが削除されます。これはデータを失う操作です。
- OSのshutdownはEC2のstopとして扱う設定です。再起動時は停止タイマーも新しく開始します。自動割り当てのパブリックIPは停止・再開で変わることがあるため、EC2 Consoleで現在値を確認し直します。CloudFormationの古いOutputsだけを参照し続けないでください。
- ログはローカルに7世代保存し、10MBを目安にローテーションします。CloudWatch Logsへの転送は設定していません。
- バックアップ・固定IP・ドメイン・自動スケール・公開マッチメイク・アカウント認証は今回の構成に含めません。認証のない直接接続なので、検証参加者のIPだけに制限します。

## 確認状況と公式資料

シドニーのEC2で Dedicated Server 版を起動し、Windowsクライアント3つの同時接続、途中退出後のNPC補充、残った2つの最終順位の一致を確認しました。別の試合では、検証用クライアント2つで建物IDごとの変更209件（うち所有者交代24件）が一致しました。記録は `Builds/LinuxOwnershipProbe-1.log` と `Builds/LinuxOwnershipProbe-2.log` にあります。

検証後、`fuchu-sydney-test` の CloudFormation スタックが `DELETE_COMPLETE`、検証EC2が `terminated` となり、検証用EBSとAWS側のSSHキーペアが削除されたことを確認しました。

- [Unity 6000.3 Desktop headless mode](https://docs.unity.com/en-us/engine/6000.3/manual/platform-specific/dedicated-server/desktop-headless-mode)
- [CloudFormation EC2 Instance](https://docs.aws.amazon.com/AWSCloudFormation/latest/TemplateReference/aws-resource-ec2-instance.html)
- [Canonical Ubuntu AMI / SSM](https://ubuntu.com/aws/docs/aws-how-to/security/use-secureboot-and-vtpm/)
- [EC2のOS shutdown動作](https://docs.aws.amazon.com/AWSEC2/latest/UserGuide/Using_ChangingInstanceInitiatedShutdownBehavior.html)
- [停止中のEBS課金](https://docs.aws.amazon.com/AWSEC2/latest/UserGuide/how-ec2-instance-stop-start-works.html)
