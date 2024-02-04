using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MvCamCtrl.NET;

namespace HikCam
{
    public class HikCamera
    {
        MyCamera.MV_CC_DEVICE_INFO_LIST m_pDeviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
        public bool m_bGrabbing { get; set; } = false;          //相机是否开始采集
        public bool ifccdConnected = false;          //相机是否初始化成功
        public string strUserID { get; set; } = string.Empty;       //自定义相机名称
        private MyCamera Camera { get; set; } = new MyCamera();
        private Bitmap image = null;
        private double runTime = 0;
        object obj = new object();
        public string SN { get; set; }
        public string Description { get; set; }
        public int Id { get; set; }

        // ch:用于从驱动获取图像的缓存 | en:Buffer for getting image from driver
        UInt32 m_nBufSizeForDriver = 8000 * 11000 * 3;
        byte[] m_pBufForDriver = new byte[8000 * 11000 * 3];

        // ch:用于保存图像的缓存 | en:Buffer for saving image
        UInt32 m_nBufSizeForSaveImage = 8000 * 11000 * 3 * 3 + 2048;
        byte[] m_pBufForSaveImage = new byte[8000 * 11000 * 3 * 3 + 2048];

        //采集回调函数
        //public static MyCamera.cbOutputdelegate ImageCallback;
        MyCamera.cbOutputExdelegate ImageCallback;
        public Action<Bitmap, int> AfterImageAcquiredEvent = null;
        public int imageIndex { get; set; } = 0;
        /// <summary>
        /// 设备个数获取
        /// </summary>
        public int DeviceNum
        {
            get
            {
                return (int)(m_pDeviceList.nDeviceNum);
            }
            set
            {

            }
        }



        /// <summary>
        /// 根据相机UserID实例化相机（对比SN更加实用）
        /// </summary>
        /// <param name="UserID"></用户自定义相机名称>
        public HikCamera(string UserID, string TriggerMode, int LineRate)
        {
            try
            {
                int nRet;
                this.strUserID = UserID;
                bool ifConnected = false;

                nRet = MyCamera.MV_CC_EnumDevices_NET(MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE, ref m_pDeviceList);
                if (m_pDeviceList.nDeviceNum == 0)
                {
                    Thread.Sleep(300);
                    nRet = MyCamera.MV_CC_EnumDevices_NET(MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE, ref m_pDeviceList);
                    if (MyCamera.MV_OK != nRet)
                    {
                        ifConnected = false;
                        MessageBox.Show("相机列表为空，请检查相机连接！");
                        return;
                    }

                }
                for (int i = 0; i < m_pDeviceList.nDeviceNum; i++)
                {
                    MyCamera.MV_CC_DEVICE_INFO device = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(m_pDeviceList.pDeviceInfo[i], typeof(MyCamera.MV_CC_DEVICE_INFO));
                    // 默认使用GIGE接口
                    IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(device.SpecialInfo.stGigEInfo, 0);
                    MyCamera.MV_GIGE_DEVICE_INFO gigeInfo = (MyCamera.MV_GIGE_DEVICE_INFO)Marshal.PtrToStructure(buffer, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                    //通过UserID打开设备
                    if (gigeInfo.chUserDefinedName == UserID)
                    {
                        // 创建相机
                        nRet = Camera.MV_CC_CreateDevice_NET(ref device);
                        if (MyCamera.MV_OK != nRet)
                        {
                            ifccdConnected = false;
                            MessageBox.Show("相机创建失败！");
                            return;
                        }
                        ifccdConnected = CamSetGrab(TriggerMode, LineRate);//OpenCamera & setTriggerMode &startGrabbing
                        ifConnected = true;
                    }
                }
                if (!ifConnected)
                {
                    ifConnected = false;
                    ifccdConnected = false;
                    //MessageBox.Show("未查询到名称为 " + UserID + " 的相机");
                }
            }
            catch (Exception)
            {
                ifccdConnected = false;
                MessageBox.Show("相机实例化异常");
            }
        }

        public bool CamSetGrab(string triggerMode, int LineRate)
        {
            try
            {
                //打开相机
                int nRet = Camera.MV_CC_OpenDevice_NET();
                if (MyCamera.MV_OK != nRet)
                {
                    Camera.MV_CC_DestroyDevice_NET();
                    MessageBox.Show($"相机打开失败!, [{nRet}]");
                    return false;
                }

                //设置默认工作模式
                Camera.MV_CC_SetEnumValue_NET("AcquisitionMode", (uint)MyCamera.MV_CAM_ACQUISITION_MODE.MV_ACQ_MODE_CONTINUOUS);// ch:工作在连续模式 | en:Acquisition On Continuous Mode

                if (triggerMode == "TriggerModeOff")//触发off
                {
                    Camera.MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF); //触发模式Off
                }
                else if (triggerMode == "SoftTrigger")//软触发
                {
                    Camera.MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON); //触发模式打开
                    Camera.MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE); //软触发 7
                    ImageCallback = new MyCamera.cbOutputExdelegate(ImageCallbackFunc);
                    Camera.MV_CC_RegisterImageCallBackEx_NET(ImageCallback, IntPtr.Zero);//注册回调函数
                }
                else if (triggerMode == "Line0Trigger_AreaCam")//Line0
                {
                    Camera.MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON); //触发模式打开
                    Camera.MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0); //硬触发 Line0
                    ImageCallback = new MyCamera.cbOutputExdelegate(ImageCallbackFunc);
                    Camera.MV_CC_RegisterImageCallBackEx_NET(ImageCallback, IntPtr.Zero);//注册回调函数
                }
                else if (triggerMode == "Line0Trigger_LineCam")//Line0  设置行频（线扫相机）
                {
                    Camera.MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON); //触发模式打开
                    Camera.MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0); //硬触发 Line0
                    Camera.MV_CC_SetBoolValue_NET("AcquisitionLineRateEnable", true);//行频控制打开
                    nRet = Camera.MV_CC_SetIntValue_NET("AcquisitionLineRate", (uint)LineRate);//设置行频（线扫相机）
                    if (nRet != MyCamera.MV_OK)
                    {
                        MessageBox.Show("行频设置失败！");
                    }
                    ImageCallback = new MyCamera.cbOutputExdelegate(ImageCallbackFunc);
                    Camera.MV_CC_RegisterImageCallBackEx_NET(ImageCallback, IntPtr.Zero);//注册回调函数

                }

                //默认开始抓图
                nRet = Camera.MV_CC_StartGrabbing_NET();
                if (MyCamera.MV_OK != nRet)
                {
                    Camera.MV_CC_DestroyDevice_NET();
                    MessageBox.Show($"触发采集失败!, [{nRet}]");
                    return false;
                }
                m_bGrabbing = true;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool CamLineRateSet(int LineRate)
        {
            int nRet = Camera.MV_CC_StopGrabbing_NET();//停止采集
            if (MyCamera.MV_OK != nRet)
            {
                return false;
            }
            Camera.MV_CC_SetBoolValue_NET("AcquisitionLineRateEnable", true);//行频控制打开
            if (MyCamera.MV_OK != nRet)
            {
                return false;
            }
            nRet = Camera.MV_CC_SetIntValue_NET("AcquisitionLineRate", (uint)LineRate);//设置行频（线扫相机）
            if (MyCamera.MV_OK != nRet)
            {
                return false;
            }
            nRet = Camera.MV_CC_StartGrabbing_NET();
            if (MyCamera.MV_OK != nRet)
            {
                return false;
            }
            return true;
        }

        private void ImageCallbackFunc(IntPtr pData, ref MyCamera.MV_FRAME_OUT_INFO_EX stFrameInfo, IntPtr pUser)
        {

            int nRet;
            Bitmap bmp = null;
            UInt32 nPayloadSize = 0;
            MyCamera.MVCC_INTVALUE stParam = new MyCamera.MVCC_INTVALUE();
            nRet = Camera.MV_CC_GetIntValue_NET("PayloadSize", ref stParam);
            if (MyCamera.MV_OK != nRet)
            {
                ShowErrorMsg("Get PayloadSize failed", nRet);
            }
            nPayloadSize = stParam.nCurValue;
            if (nPayloadSize > m_nBufSizeForDriver)
            {
                m_nBufSizeForDriver = nPayloadSize;
                m_pBufForDriver = new byte[m_nBufSizeForDriver];

                // ch:同时对保存图像的缓存做大小判断处理 | en:Determine the buffer size to save image
                // ch:BMP图片大小：width * height * 3 + 2048(预留BMP头大小) | en:BMP image size: width * height * 3 + 2048 (Reserved for BMP header)
                m_nBufSizeForSaveImage = m_nBufSizeForDriver * 3 + 2048;
                m_pBufForSaveImage = new byte[m_nBufSizeForSaveImage];
            }

            MyCamera.MvGvspPixelType enDstPixelType = new MyCamera.MvGvspPixelType();
            if (IsMonoData(stFrameInfo.enPixelType))
            {
                enDstPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8;
            }
            else if (IsColorData(stFrameInfo.enPixelType))
            {
                enDstPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_RGB8_Packed;
            }
            else
            {
                ShowErrorMsg("No such pixel type!", 0);
            }

            IntPtr pImage = Marshal.UnsafeAddrOfPinnedArrayElement(m_pBufForSaveImage, 0);
            //MyCamera.MV_SAVE_IMAGE_PARAM_EX stSaveParam = new MyCamera.MV_SAVE_IMAGE_PARAM_EX();
            MyCamera.MV_PIXEL_CONVERT_PARAM stConverPixelParam = new MyCamera.MV_PIXEL_CONVERT_PARAM();
            stConverPixelParam.nWidth = stFrameInfo.nWidth;
            stConverPixelParam.nHeight = stFrameInfo.nHeight;
            stConverPixelParam.pSrcData = pData;
            stConverPixelParam.nSrcDataLen = stFrameInfo.nFrameLen;
            stConverPixelParam.enSrcPixelType = stFrameInfo.enPixelType;
            stConverPixelParam.enDstPixelType = enDstPixelType;
            stConverPixelParam.pDstBuffer = pImage;
            stConverPixelParam.nDstBufferSize = m_nBufSizeForSaveImage;
            nRet = Camera.MV_CC_ConvertPixelType_NET(ref stConverPixelParam);
            if (MyCamera.MV_OK != nRet)
            {
            }
            if (enDstPixelType == MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8)
            {
                //************************Mono8 转 Bitmap*******************************
                bmp = new Bitmap(stFrameInfo.nWidth, stFrameInfo.nHeight, stFrameInfo.nWidth * 1, PixelFormat.Format8bppIndexed, pImage);

                ColorPalette cp = bmp.Palette;
                // init palette
                for (int i = 0; i < 256; i++)
                {
                    cp.Entries[i] = Color.FromArgb(i, i, i);
                }
                // set palette back
                bmp.Palette = cp;
            }
            else
            {
                //*********************RGB8 转 Bitmap**************************
                for (int i = 0; i < stFrameInfo.nHeight; i++)
                {
                    for (int j = 0; j < stFrameInfo.nWidth; j++)
                    {
                        byte chRed = m_pBufForSaveImage[i * stFrameInfo.nWidth * 3 + j * 3];
                        m_pBufForSaveImage[i * stFrameInfo.nWidth * 3 + j * 3] = m_pBufForSaveImage[i * stFrameInfo.nWidth * 3 + j * 3 + 2];
                        m_pBufForSaveImage[i * stFrameInfo.nWidth * 3 + j * 3 + 2] = chRed;
                    }
                }
                try
                {
                    bmp = new Bitmap(stFrameInfo.nWidth, stFrameInfo.nHeight, stFrameInfo.nWidth * 3, PixelFormat.Format24bppRgb, pImage);
                }
                catch
                {
                }
            }

            imageIndex++;
            AfterImageAcquiredEvent(bmp, imageIndex);//
        }


        /// <summary>
        /// 软触发获取单张图片
        /// </summary>
        /// <returns></returns>
        public Bitmap GetImage()
        {
            SetOffTriggerMode();

            int nRet;
            Bitmap bmp = null;

            UInt32 nPayloadSize = 0;
            MyCamera.MVCC_INTVALUE stParam = new MyCamera.MVCC_INTVALUE();
            nRet = Camera.MV_CC_GetIntValue_NET("PayloadSize", ref stParam);
            if (MyCamera.MV_OK != nRet)
            {
                //ShowErrorMsg("Get PayloadSize failed", nRet);
                return null;
            }
            nPayloadSize = stParam.nCurValue;
            if (nPayloadSize > m_nBufSizeForDriver)
            {
                m_nBufSizeForDriver = nPayloadSize;
                m_pBufForDriver = new byte[m_nBufSizeForDriver];

                // ch:同时对保存图像的缓存做大小判断处理 | en:Determine the buffer size to save image
                // ch:BMP图片大小：width * height * 3 + 2048(预留BMP头大小) | en:BMP image size: width * height * 3 + 2048 (Reserved for BMP header)
                m_nBufSizeForSaveImage = m_nBufSizeForDriver * 3 + 2048;
                m_pBufForSaveImage = new byte[m_nBufSizeForSaveImage];
            }

            IntPtr pData = Marshal.UnsafeAddrOfPinnedArrayElement(m_pBufForDriver, 0);
            MyCamera.MV_FRAME_OUT_INFO_EX stFrameInfo = new MyCamera.MV_FRAME_OUT_INFO_EX();
            // ch:超时获取一帧，超时时间为1秒 | en:Get one frame timeout, timeout is 1 sec
            nRet = Camera.MV_CC_GetOneFrameTimeout_NET(pData, m_nBufSizeForDriver, ref stFrameInfo, 1000);
            if (MyCamera.MV_OK != nRet)
            {
                ShowErrorMsg("No Data!", nRet);
                return null;
            }

            MyCamera.MvGvspPixelType enDstPixelType;
            if (IsMonoData(stFrameInfo.enPixelType))
            {
                enDstPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8;
            }
            else if (IsColorData(stFrameInfo.enPixelType))
            {
                enDstPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_RGB8_Packed;
            }
            else
            {
                ShowErrorMsg("No such pixel type!", 0);
                return null;
            }

            IntPtr pImage = Marshal.UnsafeAddrOfPinnedArrayElement(m_pBufForSaveImage, 0);
            //MyCamera.MV_SAVE_IMAGE_PARAM_EX stSaveParam = new MyCamera.MV_SAVE_IMAGE_PARAM_EX();
            MyCamera.MV_PIXEL_CONVERT_PARAM stConverPixelParam = new MyCamera.MV_PIXEL_CONVERT_PARAM();
            stConverPixelParam.nWidth = stFrameInfo.nWidth;
            stConverPixelParam.nHeight = stFrameInfo.nHeight;
            stConverPixelParam.pSrcData = pData;
            stConverPixelParam.nSrcDataLen = stFrameInfo.nFrameLen;
            stConverPixelParam.enSrcPixelType = stFrameInfo.enPixelType;
            stConverPixelParam.enDstPixelType = enDstPixelType;
            stConverPixelParam.pDstBuffer = pImage;
            stConverPixelParam.nDstBufferSize = m_nBufSizeForSaveImage;
            nRet = Camera.MV_CC_ConvertPixelType_NET(ref stConverPixelParam);
            if (MyCamera.MV_OK != nRet)
            {
                return null;
            }

            if (enDstPixelType == MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8)
            {
                //************************Mono8 转 Bitmap*******************************
                bmp = new Bitmap(stFrameInfo.nWidth, stFrameInfo.nHeight, stFrameInfo.nWidth * 1, PixelFormat.Format8bppIndexed, pImage);

                ColorPalette cp = bmp.Palette;
                // init palette
                for (int i = 0; i < 256; i++)
                {
                    cp.Entries[i] = Color.FromArgb(i, i, i);
                }
                // set palette back
                bmp.Palette = cp;
                return bmp;
            }
            else
            {
                //*********************RGB8 转 Bitmap**************************
                for (int i = 0; i < stFrameInfo.nHeight; i++)
                {
                    for (int j = 0; j < stFrameInfo.nWidth; j++)
                    {
                        byte chRed = m_pBufForSaveImage[i * stFrameInfo.nWidth * 3 + j * 3];
                        m_pBufForSaveImage[i * stFrameInfo.nWidth * 3 + j * 3] = m_pBufForSaveImage[i * stFrameInfo.nWidth * 3 + j * 3 + 2];
                        m_pBufForSaveImage[i * stFrameInfo.nWidth * 3 + j * 3 + 2] = chRed;
                    }
                }
                try
                {
                    bmp = new Bitmap(stFrameInfo.nWidth, stFrameInfo.nHeight, stFrameInfo.nWidth * 3, PixelFormat.Format24bppRgb, pImage);
                    return bmp;
                }
                catch
                {
                    return null;
                }
            }
        }



        /// <summary>
        /// 触发模式OFF
        /// </summary>
        public void SetOffTriggerMode()
        {
            try
            {
                Camera.MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF); //触发模式Off
            }
            catch (Exception)
            {
                MessageBox.Show("触发模式Off失败!");
            }
        }
        /// <summary>
        /// 触发模式ON
        /// </summary>
        public void SetOnTriggerMode()
        {
            try
            {
                Camera.MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON); //触发模式On
            }
            catch (Exception)
            {
                MessageBox.Show("触发模式On失败!");
            }
        }
        /// <summary>
        /// 设为软触发
        /// </summary>
        public void SetSoftwareTrigger()
        {
            try
            {
                Camera.MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON); //触发模式打开
                Camera.MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE); //软触发 7
            }
            catch (Exception)
            {
                MessageBox.Show("软触发设置失败!");
            }
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="TrigSource"></// ch:触发源选择:
        /// 0 - Line0; | en:Trigger source select:0 - Line0;
        //           1 - Line1;
        //           2 - Line2;
        //           3 - Line3;
        //           4 - Counter;
        public void SetHardTrigger()
        {
            try
            {
                Camera.MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON); //触发模式打开
                Camera.MV_CC_SetEnumValue_NET("TriggerSource", (uint)MyCamera.MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0); //硬触发 Line0
            }
            catch (Exception)
            {
                MessageBox.Show("硬触发设置失败!");
            }

        }

        /// <summary>
        /// 打开相机
        /// </summary>
        public void OpenCamSoftTriger()
        {
            try
            {
                int nRet = -1;
                // 打开相机
                nRet = Camera.MV_CC_OpenDevice_NET();
                if (MyCamera.MV_OK != nRet)
                {
                    MessageBox.Show("打开此相机失败");
                    return;
                }

                // 探测网络最佳包大小----GIGE接口有，USB报错
                int nPacketSize = Camera.MV_CC_GetOptimalPacketSize_NET();
                if (nPacketSize > 0)
                {
                    nRet = Camera.MV_CC_SetIntValue_NET("GevSCPSPacketSize", (uint)nPacketSize);
                    if (nRet != MyCamera.MV_OK)
                    {
                        MessageBox.Show("Warning: Set Packet Size failed");
                    }
                }
                else
                {
                    MessageBox.Show("Warning: Set Packet Size failed");
                }

                // 获取包大小
                MyCamera.MVCC_INTVALUE stParam = new MyCamera.MVCC_INTVALUE();
                nRet = Camera.MV_CC_GetIntValue_NET("PayloadSize", ref stParam);
                if (MyCamera.MV_OK != nRet)
                {
                    MessageBox.Show("Get PayloadSize Fail");
                    return;
                }
                SetSoftwareTrigger();//设置软触发



            }
            catch (Exception)
            {
                throw;
            }

        }


        public void OpenCamHardTriger(uint Line)
        {
            try
            {
                int nRet = -1;
                // 打开相机
                nRet = Camera.MV_CC_OpenDevice_NET();
                if (MyCamera.MV_OK != nRet)
                {
                    MessageBox.Show("打开此相机失败");
                    return;
                }

                // 探测网络最佳包大小----GIGE接口有，USB报错
                int nPacketSize = Camera.MV_CC_GetOptimalPacketSize_NET();
                if (nPacketSize > 0)
                {
                    nRet = Camera.MV_CC_SetIntValue_NET("GevSCPSPacketSize", (uint)nPacketSize);
                    if (nRet != MyCamera.MV_OK)
                    {
                        MessageBox.Show("Warning: Set Packet Size failed");
                    }
                }
                else
                {
                    MessageBox.Show("Warning: Set Packet Size failed");
                }

                // 获取包大小
                MyCamera.MVCC_INTVALUE stParam = new MyCamera.MVCC_INTVALUE();
                nRet = Camera.MV_CC_GetIntValue_NET("PayloadSize", ref stParam);
                if (MyCamera.MV_OK != nRet)
                {
                    MessageBox.Show("Get PayloadSize Fail");
                    return;
                }

                //ImageCallback = new MyCamera.cbOutputExdelegate(ImageCallbackFunc);
                //Camera.MV_CC_RegisterImageCallBackEx_NET(ImageCallback, IntPtr.Zero);//注册回调函数
                SetHardTrigger();
            }
            catch (Exception)
            {
                throw;
            }

        }

        /// <summary>
        /// 关闭相机
        /// </summary>
        public void CloseCam()
        {
            try
            {
                int nRet = -1;
                if (m_bGrabbing)
                {
                    StopGrab();
                }
                nRet = Camera.MV_CC_CloseDevice_NET();
            }
            catch (Exception ex)
            {
                MessageBox.Show("关闭相机异常 \n" + ex.ToString());
            }
        }

        /// <summary>
        ///  销毁相机
        /// </summary>
        public void DestoryCam()
        {
            try
            {
                int nRet = -1;
                nRet = Camera.MV_CC_DestroyDevice_NET();
                if (MyCamera.MV_OK != nRet)
                {
                    MessageBox.Show("销毁相机失败！ \n");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("销毁相机异常 \n" + ex.ToString());
            }
        }


        /// <summary>
        /// 开始连续采集
        /// </summary>
        public void StartGrab()
        {
            int nRet = -1;

            nRet = Camera.MV_CC_StartGrabbing_NET();
            if (MyCamera.MV_OK != nRet)
            {
                MessageBox.Show("开始采集失败");
                return;
            }
            m_bGrabbing = true;
        }

        public void ShowPic(PictureBox picBox)
        {
            int nRet = -1;
            nRet = Camera.MV_CC_Display_NET(picBox.Handle);
            if (MyCamera.MV_OK != nRet)
            {
                MessageBox.Show("显示失败");
            }
        }

        //public void ShowOnModule(PicShowControl myControl)
        //{

        //}

        /// <summary>
        /// 停止连续采集
        /// </summary>
        public void StopGrab()
        {
            int nRet = -1;

            nRet = Camera.MV_CC_StopGrabbing_NET();
            if (nRet != MyCamera.MV_OK)
            {
                MessageBox.Show("停止采集失败! " + nRet.ToString());
            }
            if (m_bGrabbing)
            {
                m_bGrabbing = false;
            }
        }

        /// <summary>
        /// 相机软触发单次
        /// </summary>
        /// <returns></returns>
        public bool TriggerOnce()
        {
            Camera.MV_CC_SetEnumValue_NET("TriggerMode", 1);
            int nRet = -1;
            //ch: 触发命令 | en:Trigger command
            nRet = Camera.MV_CC_SetCommandValue_NET("TriggerSoftware");
            if (MyCamera.MV_OK == nRet)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        public double Exposure
        {
            get
            {
                MyCamera.MVCC_FLOATVALUE stParam = new MyCamera.MVCC_FLOATVALUE();
                int nRet = Camera.MV_CC_GetFloatValue_NET("ExposureTime", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    return stParam.fCurValue;
                }
                else
                {
                    return 0;
                }
            }
            set
            {
                Camera.MV_CC_SetEnumValue_NET("ExposureAuto", 0);
                int nRet = Camera.MV_CC_SetFloatValue_NET("ExposureTime", (float)value);
                if (nRet != MyCamera.MV_OK)
                {
                    //MessageBox.Show("SetExposure NG");
                    Camera.MV_CC_SetFloatValue_NET("ExposureTime", (float)value);
                    return;

                }
            }
        }

        public double FrameRate
        {
            get
            {
                MyCamera.MVCC_FLOATVALUE stParam = new MyCamera.MVCC_FLOATVALUE();
                int nRet = Camera.MV_CC_GetFloatValue_NET("AcquisitionFrameRate", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    return stParam.fCurValue;
                }
                else
                {
                    return 0;
                }
            }
            set
            {
                int nRet = Camera.MV_CC_SetFloatValue_NET("AcquisitionFrameRate", (float)value);
                if (nRet != MyCamera.MV_OK)
                {
                    return;
                }
            }
        }

        public double Gain
        {
            get
            {
                MyCamera.MVCC_FLOATVALUE stParam = new MyCamera.MVCC_FLOATVALUE();
                int nRet = Camera.MV_CC_GetFloatValue_NET("Gain", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    return stParam.fCurValue;
                }
                else
                {
                    return 0;
                }
            }
            set
            {
                Camera.MV_CC_SetEnumValue_NET("GainAuto", 0);
                int nRet = Camera.MV_CC_SetFloatValue_NET("Gain", (float)value);
                if (nRet != MyCamera.MV_OK)
                {
                    return;
                }
            }
        }

        public double Gamma
        {
            get
            {
                MyCamera.MVCC_FLOATVALUE stParam = new MyCamera.MVCC_FLOATVALUE();
                int nRet = Camera.MV_CC_GetFloatValue_NET("Gamma", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    return stParam.fCurValue;
                }
                else
                {
                    return 0;
                }
            }
            set
            {
                Camera.MV_CC_SetEnumValue_NET("Gamma", 0);
                int nRet = Camera.MV_CC_SetFloatValue_NET("Gain", (float)value);
                if (nRet != MyCamera.MV_OK)
                {
                    return;
                }
            }
        }

        public double BurstFrameCount //单次触发图片数量 需stopgraping再set
        {
            get
            {
                MyCamera.MVCC_INTVALUE stParam = new MyCamera.MVCC_INTVALUE();
                int nRet = Camera.MV_CC_GetIntValue_NET("AcquisitionBurstFrameCount", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    return stParam.nCurValue;
                }
                else
                {
                    return 0;
                }
            }
            set
            {
                int nRet = Camera.MV_CC_StopGrabbing_NET();
                if (nRet != MyCamera.MV_OK)
                {
                    return;
                }
                nRet = Camera.MV_CC_SetIntValue_NET("AcquisitionBurstFrameCount", (uint)value);
                if (nRet != MyCamera.MV_OK)
                {
                    return;
                }
                nRet = Camera.MV_CC_StartGrabbing_NET();
                if (nRet != MyCamera.MV_OK)
                {
                    return;
                }
            }
        }


        public double LineRate //线扫相机行频设置
        {
            get
            {
                MyCamera.MVCC_INTVALUE stParam = new MyCamera.MVCC_INTVALUE();
                int nRet = Camera.MV_CC_GetIntValue_NET("AcquisitionBurstFrameCount", ref stParam);
                if (MyCamera.MV_OK == nRet)
                {
                    return stParam.nCurValue;
                }
                else
                {
                    return 0;
                }
            }
            set
            {
                int nRet = Camera.MV_CC_SetBoolValue_NET("AcquisitionLineRateEnable", true);
                if (nRet != MyCamera.MV_OK)
                {
                    return;
                }
                nRet = Camera.MV_CC_SetIntValue_NET("AcquisitionLineRate", (uint)value);
                if (nRet != MyCamera.MV_OK)
                {
                    return;
                }
            }
        }
        //public Bitmap Image
        //{
        //    get
        //    {
        //        Bitmap bitmap = (Bitmap)image.Clone();
        //        image = null;
        //        return bitmap;
        //    }
        //    private set
        //    {
        //        image = value;
        //    }
        //}

        public static T DeepCopyByBin<T>(T obj)
        {
            object retval;
            using (MemoryStream ms = new MemoryStream())
            {
                BinaryFormatter bf = new BinaryFormatter();
                //序列化成流
                bf.Serialize(ms, obj);
                ms.Seek(0, SeekOrigin.Begin);
                //反序列化成对象
                retval = bf.Deserialize(ms);
                ms.Close();
            }
            return (T)retval;
        }



        public bool Close()
        {
            int nRet = Camera.MV_CC_StopGrabbing_NET();
            if (MyCamera.MV_OK != nRet)
            {
                System.Windows.Forms.MessageBox.Show($"Stop Device [ SN={SN} ] Fail！\n [ErrorCode={nRet}]");
                return false;
            }
            m_bGrabbing = false;
            nRet = Camera.MV_CC_CloseDevice_NET();
            if (MyCamera.MV_OK != nRet)
            {
                System.Windows.Forms.MessageBox.Show($"Close Device [ SN={SN} ] Fail！\n [ErrorCode={nRet}]");
                return false;
            }
            return true;
        }
        private void ImageCallbackFunc(IntPtr pData, ref MyCamera.MV_FRAME_OUT_INFO pFrameInfo, IntPtr pUser)
        {
            MyCamera.MV_FRAME_OUT_INFO frameInfo = pFrameInfo;
            Task.Run(() =>
            {
                lock (obj)
                {
                    ImageGet(pData, frameInfo);
                }
            });
        }

        private Bitmap GetImage(IntPtr pData, MyCamera.MV_FRAME_OUT_INFO stFrameInfo)
        {
            Bitmap bmp = null;
            {
                int nRet;
                // ch:用于保存图像的缓存 | en:Buffer for saving image
                uint m_nBufSizeForSaveImage = 3072 * 2048 * 3 * 3 + 2048;
                byte[] m_pBufForSaveImage = new byte[3072 * 2048 * 3 * 3 + 2048];

                if ((3 * stFrameInfo.nFrameLen + 2048) > m_nBufSizeForSaveImage)
                {
                    m_nBufSizeForSaveImage = 3 * stFrameInfo.nFrameLen + 2048;
                    m_pBufForSaveImage = new byte[m_nBufSizeForSaveImage];
                }

                IntPtr pImage = Marshal.UnsafeAddrOfPinnedArrayElement(m_pBufForSaveImage, 0);
                MyCamera.MV_SAVE_IMAGE_PARAM_EX stSaveParam = new MyCamera.MV_SAVE_IMAGE_PARAM_EX();
                stSaveParam.enImageType = MyCamera.MV_SAVE_IAMGE_TYPE.MV_Image_Bmp;
                stSaveParam.enPixelType = stFrameInfo.enPixelType;
                stSaveParam.pData = pData;
                stSaveParam.nDataLen = stFrameInfo.nFrameLen;
                stSaveParam.nHeight = stFrameInfo.nHeight;
                stSaveParam.nWidth = stFrameInfo.nWidth;
                stSaveParam.pImageBuffer = pImage;
                stSaveParam.nBufferSize = m_nBufSizeForSaveImage;
                stSaveParam.nJpgQuality = 80;
                nRet = Camera.MV_CC_SaveImageEx_NET(ref stSaveParam);
                if (MyCamera.MV_OK == nRet)
                {
                    using (MemoryStream stream = new MemoryStream(m_pBufForSaveImage))
                    {
                        bmp = new Bitmap(stream);
                        stream.Flush();
                        stream.Close();
                        stream.Dispose();
                    }
                    ColorPalette cp = bmp.Palette;
                    // init palette
                    for (int i = 0; i < 256; i++)
                    {
                        cp.Entries[i] = Color.FromArgb(i, i, i);
                    }
                    // set palette back
                    bmp.Palette = cp;
                }
            }
            GC.Collect();
            return bmp;
        }
        private bool IsMonoData(MyCamera.MvGvspPixelType enGvspPixelType)
        {
            switch (enGvspPixelType)
            {
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono10:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono10_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono12:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono12_Packed:
                    return true;

                default:
                    return false;
            }
        }
        private void ImageGet(IntPtr pData, MyCamera.MV_FRAME_OUT_INFO pFrameInfo)
        {
            Bitmap image2 = GetImage(pData, pFrameInfo);
            bool imgOk = image2 != null;
        }

        /************************************************************************
         *  @fn     IsColorData()
         *  @brief  判断是否是彩色数据
         *  @param  enGvspPixelType         [IN]           像素格式
         *  @return 成功，返回0；错误，返回-1 
         ************************************************************************/
        private bool IsColorData(MyCamera.MvGvspPixelType enGvspPixelType)
        {
            switch (enGvspPixelType)
            {
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGR8:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerRG8:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGB8:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerBG8:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGR10:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerRG10:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGB10:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerBG10:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGR12:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerRG12:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGB12:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerBG12:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGR10_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerRG10_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGB10_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerBG10_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGR12_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerRG12_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGB12_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerBG12_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_RGB8_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_YUV422_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_YUV422_YUYV_Packed:
                case MyCamera.MvGvspPixelType.PixelType_Gvsp_YCBCR411_8_CBYYCRYY:
                    return true;

                default:
                    return false;
            }
        }

        public void SaveBmpImg(string FileName)
        {
            string Path = "D://视觉脚本文件//Images";

            int nRet;
            UInt32 nPayloadSize = 0;
            MyCamera.MVCC_INTVALUE stParam = new MyCamera.MVCC_INTVALUE();
            nRet = Camera.MV_CC_GetIntValue_NET("PayloadSize", ref stParam);
            if (MyCamera.MV_OK != nRet)
            {
                MessageBox.Show("Get PayloadSize failed");
                return;
            }
            nPayloadSize = stParam.nCurValue;
            if (nPayloadSize > m_nBufSizeForDriver)
            {
                m_nBufSizeForDriver = nPayloadSize;
                m_pBufForDriver = new byte[m_nBufSizeForDriver];

                // ch:同时对保存图像的缓存做大小判断处理 | en:Determine the buffer size to save image
                // ch:BMP图片大小：width * height * 3 + 2048(预留BMP头大小) | en:BMP image size: width * height * 3 + 2048 (Reserved for BMP header)
                m_nBufSizeForSaveImage = m_nBufSizeForDriver * 3 + 2048;
                m_pBufForSaveImage = new byte[m_nBufSizeForSaveImage];
            }

            IntPtr pData = Marshal.UnsafeAddrOfPinnedArrayElement(m_pBufForDriver, 0);
            MyCamera.MV_FRAME_OUT_INFO_EX stFrameInfo = new MyCamera.MV_FRAME_OUT_INFO_EX();
            // ch:超时获取一帧，超时时间为1秒 | en:Get one frame timeout, timeout is 1 sec
            nRet = Camera.MV_CC_GetOneFrameTimeout_NET(pData, m_nBufSizeForDriver, ref stFrameInfo, 1000);
            if (MyCamera.MV_OK != nRet)
            {
                MessageBox.Show("No Data!");
                return;
            }

            MyCamera.MvGvspPixelType enDstPixelType;
            if (IsMonoData(stFrameInfo.enPixelType))
            {
                enDstPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8;
            }
            else if (IsColorData(stFrameInfo.enPixelType))
            {
                enDstPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_RGB8_Packed;
            }
            else
            {
                MessageBox.Show("No such pixel type!");
                return;
            }

            IntPtr pImage = Marshal.UnsafeAddrOfPinnedArrayElement(m_pBufForSaveImage, 0);
            //MyCamera.MV_SAVE_IMAGE_PARAM_EX stSaveParam = new MyCamera.MV_SAVE_IMAGE_PARAM_EX();
            MyCamera.MV_PIXEL_CONVERT_PARAM stConverPixelParam = new MyCamera.MV_PIXEL_CONVERT_PARAM();
            stConverPixelParam.nWidth = stFrameInfo.nWidth;
            stConverPixelParam.nHeight = stFrameInfo.nHeight;
            stConverPixelParam.pSrcData = pData;
            stConverPixelParam.nSrcDataLen = stFrameInfo.nFrameLen;
            stConverPixelParam.enSrcPixelType = stFrameInfo.enPixelType;
            stConverPixelParam.enDstPixelType = enDstPixelType;
            stConverPixelParam.pDstBuffer = pImage;
            stConverPixelParam.nDstBufferSize = m_nBufSizeForSaveImage;
            nRet = Camera.MV_CC_ConvertPixelType_NET(ref stConverPixelParam);
            if (MyCamera.MV_OK != nRet)
            {
                return;
            }

            if (enDstPixelType == MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8)
            {
                //************************Mono8 转 Bitmap*******************************
                Bitmap bmp = new Bitmap(stFrameInfo.nWidth, stFrameInfo.nHeight, stFrameInfo.nWidth * 1, PixelFormat.Format8bppIndexed, pImage);

                ColorPalette cp = bmp.Palette;
                // init palette
                for (int i = 0; i < 256; i++)
                {
                    cp.Entries[i] = Color.FromArgb(i, i, i);
                }
                // set palette back
                bmp.Palette = cp;
                string finalPath = Path + FileName;
                bmp.Save(finalPath, ImageFormat.Bmp);
            }
            else
            {
                //*********************RGB8 转 Bitmap**************************
                for (int i = 0; i < stFrameInfo.nHeight; i++)
                {
                    for (int j = 0; j < stFrameInfo.nWidth; j++)
                    {
                        byte chRed = m_pBufForSaveImage[i * stFrameInfo.nWidth * 3 + j * 3];
                        m_pBufForSaveImage[i * stFrameInfo.nWidth * 3 + j * 3] = m_pBufForSaveImage[i * stFrameInfo.nWidth * 3 + j * 3 + 2];
                        m_pBufForSaveImage[i * stFrameInfo.nWidth * 3 + j * 3 + 2] = chRed;
                    }
                }
                try
                {
                    Bitmap bmp = new Bitmap(stFrameInfo.nWidth, stFrameInfo.nHeight, stFrameInfo.nWidth * 3, PixelFormat.Format24bppRgb, pImage);
                    string finalPath = Path + FileName;
                    bmp.Save(finalPath, ImageFormat.Bmp);
                }
                catch
                {
                }

            }
            ShowErrorMsg("Save Succeed!", 0);
        }

        private void ShowErrorMsg(string csMessage, int nErrorNum)
        {
            string errorMsg;
            if (nErrorNum == 0)
            {
                errorMsg = csMessage;
            }
            else
            {
                errorMsg = csMessage + ": Error =" + String.Format("{0:X}", nErrorNum);
            }

            switch (nErrorNum)
            {
                case MyCamera.MV_E_HANDLE: errorMsg += " Error or invalid handle "; break;
                case MyCamera.MV_E_SUPPORT: errorMsg += " Not supported function "; break;
                case MyCamera.MV_E_BUFOVER: errorMsg += " Cache is full "; break;
                case MyCamera.MV_E_CALLORDER: errorMsg += " Function calling order error "; break;
                case MyCamera.MV_E_PARAMETER: errorMsg += " Incorrect parameter "; break;
                case MyCamera.MV_E_RESOURCE: errorMsg += " Applying resource failed "; break;
                case MyCamera.MV_E_NODATA: errorMsg += " No data "; break;
                case MyCamera.MV_E_PRECONDITION: errorMsg += " Precondition error, or running environment changed "; break;
                case MyCamera.MV_E_VERSION: errorMsg += " Version mismatches "; break;
                case MyCamera.MV_E_NOENOUGH_BUF: errorMsg += " Insufficient memory "; break;
                case MyCamera.MV_E_UNKNOW: errorMsg += " Unknown error "; break;
                case MyCamera.MV_E_GC_GENERIC: errorMsg += " General error "; break;
                case MyCamera.MV_E_GC_ACCESS: errorMsg += " Node accessing condition error "; break;
                case MyCamera.MV_E_ACCESS_DENIED: errorMsg += " No permission "; break;
                case MyCamera.MV_E_BUSY: errorMsg += " Device is busy, or network disconnected "; break;
                case MyCamera.MV_E_NETER: errorMsg += " Network error "; break;
            }

            MessageBox.Show(errorMsg, "PROMPT");
        }

    }
}
