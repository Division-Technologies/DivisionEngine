# DirectX
DirectX（Direct3D）12に関する情報をまとめる。


## 用語
DXGI（DirectX Graphics Infrastructure）
- DirectXに依存せずにデバイス管理を行うための低レベルAPIセット
    - DXGIは、アダプター（GPUやソフトウェアラスタライザなどのグラフィックスデバイス）やディスプレイなどのデバイスを列挙し、管理するための機能を提供する
    - DirectX 9まではハードウェアデバイスの列挙、レンダリング結果の出力、全画面表示への移行などの低レベルなタスクがDirectXランタイムに含まれていたので、DXGIとして分割された
        - DirectXランタイムの変更頻度と、DXGIの変更頻度が異なるため、両者を分割することで開発と保守が効率化できる
- ref: [DXGI の概要](https://learn.microsoft.com/ja-jp/windows/win32/direct3ddxgi/d3d10-graphics-programming-guide-dxgi)

コマンドリスト
- GPUに送信する命令をまとめるためのオブジェクト
    - DX11まではDeviceContextオブジェクトに対してその都度命令を送信していたが、DX12からはコマンドリストを使い命令をまとめて送信する方式に変更された
        - つまり逐次実行式から遅延実行式に変更された
- コマンドリストにまとまっている命令をGPUに送るためのオブジェクトがコマンドキュー

デスクトップ環境（Desktop Environment, DE）
- ウィンドウマネージャー、コンポジター、ステータスバー、ファイルマネージャーなどOSのデスクトップに関する機能を提供する一連のツールセット
    - デスクトップマネージャ（Desktop Manager）ともよばれる
- ウィンドウマネージャー（Window Manager, WM）
    - ウィンドウの配置、移動、リサイズなどを管理するコンポーネント
        - タイトルバー、ボーダー、スクロールバー、装飾などの表示も行う。
    - モード
        - スタッキング
            - Windows XPまでのデスクトップ環境で使用されていたモード
            - ウィンドウを描画する際に、ペインターズアルゴリズムを使用して最終的な画面表示を行うモード
                - つまり、ウィンドウを重ねる際に、各ウィンドウの画像をメモリ上で実際に上書きする
                - あるウィンドウが移動した場合、WMが各プログラムにウィンドウの再描画を要求する
            - ペインターズアルゴリズム（画家のアルゴリズム）
                - 複数のウィンドウを重ねて描画する際に、奥側のウィンドウから順に描画していくアルゴリズム
                - 要するにZソート法
        - コンポジティング
            - Windows Vista以降のデスクトップ環境で使用されているモード
            - 各ウィンドウの描画結果をメモリ上に保持しておき、それらを合成して最終的な画面表示を行うモード
                - これによりウィンドウの透明度設定やフェードイン・フェードアウトなどのエフェクトが可能になる
            - コンポジティングを行うコンポーネントをコンポジター（Compositor）という
- DWM（Desktop Window Manager）
    - Windowsに搭載されているデスクトップ環境
    - Windowsではデスクトップ環境がOSに組み込まれているため、WMやCompositorなど独立したコンポーネントを組み合わせてDEを構築するのではなく、DWMがそれらの機能を統合して提供している

全画面表示排他モード（Fullscreen Exclusive Mode, FSE）
- DWMを介さずにGPUから直接ディスプレイに描画するモード
- パフォーマンスの観点からは優位なモードだったが、他のアプリケーションと共存できないためユーザ体験が悪くなりやすい
- DX12で廃止

フルスクリーン最適化（Fullscreen Optimizations）
- 廃止されたFSEの代替手段の一つとしてボーダーレスフルスクリーン（仮想フルスクリーン）がある
    - DWMを介して描画を行うが、タイトルバーやボーダーなどを非表示にすることでフルスクリーン表示のように見せるモード
- 画面上に他のアプリケーションが存在しない状態でボーダーレスのフルスクリーンウィンドウで実行されていることをDWMが認識すると、ディスプレイ制御とCPUやGPUなどのリソースが可能な限り割り当てられるようになる
    - これによりFSEに近いパフォーマンスが得られるようになる
- ref: [Demystifying Fullscreen Optimizations](https://devblogs.microsoft.com/directx/demystifying-full-screen-optimizations/)

スワップチェーン
- 描画結果を保持する複数のバッファを管理し、描画済みのバッファをDWMへ提示するためのオブジェクト
    - 2つのバッファを使用する構成をダブルバッファリング、3つのバッファを使用する構成をトリプルバッファリングとよぶ
    - おおまかな流れ
        - 1. スワップチェーン作成時に、DXGIがBuffer 0とBuffer 1を確保する
        - 2. アプリケーションがBuffer 0に描画する
        - 3. `Present`関数でBuffer 0をDWMに提示する
        - 4. アプリケーションはBuffer 1を次の描画先として使い、DWMはBuffer 0を次回のデスクトップ合成に使用する

- DXGI Flipモデル
    - Windows 8／DXGI 1.2で導入されたスワップチェーンのモデル
    - 従来のBitBltモデルでは、Presentを呼び出すと、バックバッファの内容がDWMのリダイレクションサーフェスへコピーされる
        - このコピーにより、メモリ帯域を消費し、処理時間が増加する
    - Flipモデルでは、スワップチェーンのバックバッファがDWMと共有される
        - DWMは共有されたバックバッファを直接参照してデスクトップを合成できる
        - これにより、バックバッファからDWMのリダイレクションサーフェスへの追加コピーが不要になる
- ref:
    - [スワップ チェーン](https://learn.microsoft.com/ja-jp/windows/win32/direct3d12/swap-chains)
    - [DXGI flip model](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-flip-model)

ディスクリプタヒープ
- ビュー（View）
    - バッファやテクスチャなどのリソースに格納されているデータを、どのように解釈するかを定義する情報
        - Shader Resource View（SRV）、Unordered Access View（UAV）、Render Target View（RTV）、Depth Stencil View（DSV）などがある
    - つまりリソースの用途・見方を定めるもので、リソース本体とは別物
        - 一つのリソースに対して複数のビューを作成することもできる
            - 例：同じテクスチャをRTVとして書き込み、後のパスでSRVとして読む
    - DX11ではViewは独立したCOMオブジェクトだった
        - DX12ではオブジェクトとしては存在せず、View定義をディスクリプタとして書き込む形になった
- ディスクリプタ（Descriptor）
    - リソースの実体への参照（GPUアドレス）と、Viewとしての解釈情報（フォーマット、ミップ範囲などのメタデータ）を1つにまとめた、GPUが読める小さなデータブロック
        - リソースの実体そのものは含まず、実体はあくまでリソースヒープ（default heapなど）側にあり、ディスクリプタはそれを参照する
    - サイズは数十バイト程度（GetDescriptorHandleIncrementSizeで取得）
    - DX12からCreateShaderResourceViewなどのAPIは、View定義をディスクリプタとしてヒープの指定スロットに書き込む操作になった
- ディスクリプタヒープ（Descriptor Heap）
    - ディスクリプタをまとめて格納するための連続したメモリ領域
    - シェーダーから参照されるディスクリプタをGPUから見える場所に集約しておくことで、GPUがリソースへ効率的にアクセスできるようにする
- ディスクリプタテーブル
    - ディスクリプタヒープ内の連続したディスクリプタの範囲を指すサブセット
        - グラフィックスパイプラインはルートシグネチャを介して、テーブルの開始位置とサイズによってディスクリプタテーブルを参照し、リソースにアクセスする
![Descriptor_Heap_Table](Descriptor_Heap_Table.png)
- ref:
    - [記述子ヒープの概要](https://learn.microsoft.com/ja-jp/windows/win32/direct3d12/descriptor-heaps-overview)
    - [Descriptor Heap](https://docs.vulkan.org/guide/latest/descriptor_heap.html)
