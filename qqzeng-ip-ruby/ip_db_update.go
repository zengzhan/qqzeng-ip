// zhouziqing / 233355@gmail.com
// 2016/12/21

package imap

import (
	"io/ioutil"
	"os"
	"path/filepath"
	"time"

	"github.com/emersion/go-imap"
	imap_client "github.com/emersion/go-imap/client"
	"github.com/veqryn/go-email/email"

	"github.com/mholt/archiver"
)

type IpDatUpdatedFunc func(dat_version string)

type IpDatMailboxUpdater struct {
	quit            chan bool
	updatedCallback IpDatUpdatedFunc
	username        string
	password        string
	mailserver      string
}

func NewIpDatMailboxUpdater(server, username, password string, updatedCallback IpDatUpdatedFunc) (*IpDatMailboxUpdater, error) {
	// Connect to server
	c, err := imap_client.DialTLS(server+":993", nil)
	if err != nil {
		return nil, err
	}
	defer c.Close()

	// Login
	if err := c.Login(username, password); err != nil {
		return nil, err
	}
	defer c.Logout()

	// 恢复标记 for debug
	//if _, err := c.Select("INBOX", false); err != nil {
	//	panic(err)
	//}
	//search := &imap.SearchCriteria{
	//	From: "qqzeng-ip",
	//}
	//seq, err := c.Search(search)
	//if err != nil {
	//	panic(err)
	//}
	//seqset := new(imap.SeqSet)
	//seqset.AddNum(seq...)
	//
	//c.Store(seqset, "-FLAGS", imap.AnsweredFlag, nil)

	return &IpDatMailboxUpdater{mailserver: server, username: username, password: password, updatedCallback: updatedCallback}, nil
}

const tmpFilePath = "./tmp/"
const ipDatFilePath = "./lib/zengip.dat"

func (s *IpDatMailboxUpdater) parse(bean imap.Literal) {
	msg, err := email.ParseMessage(bean)
	if err != nil {
		panic(err)
	}
	for _, part := range msg.PartsContentTypePrefix("application/octet-stream") {
		if _, err := os.Stat(tmpFilePath); os.IsNotExist(err) {
			os.Mkdir(tmpFilePath, 0711)
		}
		_, contentDisposition, _ := part.Header.ContentDisposition()
		attrFilename := contentDisposition["filename"]
		attrBaseFilename := attrFilename[0 : len(attrFilename)-len(filepath.Ext(attrFilename))]
		rarFilePath := tmpFilePath + attrFilename
		unrarPath := tmpFilePath + attrBaseFilename

		err := ioutil.WriteFile(rarFilePath, part.Body, 0711)
		if err != nil {
			panic(err)
		}

		err = archiver.Rar.Open(rarFilePath, unrarPath)
		if err != nil {
			panic(err)
		}

		b, err := ioutil.ReadFile(unrarPath + "/qqzeng-ip-utf8.dat")
		if err != nil {
			panic(err)
		}

		err = ioutil.WriteFile(ipDatFilePath, b, 0711)
		if err != nil {
			panic(err)
		}

		err = os.Remove(rarFilePath)
		if err != nil {
			panic(err)
		}

		err = os.RemoveAll(unrarPath)
		if err != nil {
			panic(err)
		}

		s.updatedCallback(attrBaseFilename)
	}
}

func (s *IpDatMailboxUpdater) Close() {
	s.quit <- true
}

func (s *IpDatMailboxUpdater) fetch() {
	// Connect to server
	c, err := imap_client.DialTLS(s.mailserver+":993", nil)
	if err != nil {
		return
	}
	defer c.Close()

	// Login
	if err := c.Login(s.username, s.password); err != nil {
		return
	}
	defer c.Logout()

	// Select INBOX
	if _, err := c.Select("INBOX", false); err != nil {
		panic(err)
	}

	search := &imap.SearchCriteria{
		Unanswered: true,
		From:       "qqzeng-ip",
	}
	seq, err := c.Search(search)
	if err != nil {
		panic(err)
	}

	// 无新版本
	if len(seq) == 0 {
		return
	}

	seqset := new(imap.SeqSet)
	seqset.AddNum(seq...)

	// 添加回复标记
	err = c.Store(seqset, "+FLAGS", imap.AnsweredFlag, nil)
	if err != nil {
		panic(err)
	}

	ch := make(chan *imap.Message, 10)
	go func() {
		if err := c.Fetch(seqset, []string{imap.EnvelopeMsgAttr}, ch); err != nil {
			panic(err)
		}
	}()

	// 选取最新版
	var latestMsgTime time.Time
	var latestMsg *imap.Message
	for msg := range ch {
		if msg.Envelope.Date.After(latestMsgTime) {
			latestMsgTime = msg.Envelope.Date
			latestMsg = msg
		}
	}

	// 拉取邮件 body
	seqset.Clear()
	seqset.AddNum(latestMsg.SeqNum)
	msg := make(chan *imap.Message)
	go func() {
		if err := c.Fetch(seqset, []string{"BODY[]"}, msg); err != nil {
			panic(err)
		}
	}()

	latestMsg = <-msg

	s.parse(latestMsg.GetBody("BODY[]"))
}

func (s *IpDatMailboxUpdater) Run() {
	go func() {
		for {
			select {
			case <-s.quit:
				return
			default:
				s.fetch()
			}
			// 每5分钟拉取一次
			time.Sleep(time.Second * 300)
		}
	}()
}
